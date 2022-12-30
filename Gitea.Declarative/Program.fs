﻿namespace Gitea.Declarative

open System
open System.IO
open System.Net.Http
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Logging.Console
open Microsoft.Extensions.Options
open Argu

type ArgsFragments =
    | [<ExactlyOnce ; EqualsAssignmentOrSpaced>] Config_File of string
    | [<ExactlyOnce ; EqualsAssignmentOrSpaced>] Gitea_Host of string
    | [<ExactlyOnce ; EqualsAssignmentOrSpaced>] Gitea_Admin_Api_Token of string
    | [<Unique ; EqualsAssignmentOrSpaced>] GitHub_Api_Token of string

    interface IArgParserTemplate with
        member s.Usage =
            match s with
            | Config_File _ ->
                "a config file, JSON, conforming to GiteaConfig.schema.json, specifying the desired Gitea configuration"
            | Gitea_Host _ -> "the Gitea host, e.g. https://gitea.mydomain.com"
            | Gitea_Admin_Api_Token _ -> "a Gitea admin user's API token"
            | GitHub_Api_Token _ -> "a GitHub API token with read access to every desired sync-from-GitHub repo"

type Args =
    {
        ConfigFile : FileInfo
        GiteaHost : Uri
        GiteaAdminApiToken : string
        GitHubApiToken : string option
    }

module Program =

    let printUserErrors (m : Map<User, AlignmentError<UserInfo>>) =
        m |> Map.iter (fun (User u) err -> printfn $"%s{u}: {err}")

    let printRepoErrors (m : Map<User, Map<RepoName, AlignmentError<Repo>>>) =
        m
        |> Map.iter (fun (User u) errMap -> errMap |> Map.iter (fun (RepoName r) err -> printfn $"%s{u}: %s{r}: {err}"))

    let rec getUserInputDefaultNo (getUserInput : unit -> string) (message : string) : bool =
        Console.Write $"${message} (y/N): "
        let answer = getUserInput ()

        match answer with
        | "y"
        | "Y" -> true
        | "n"
        | "N"
        | "" -> false
        | _ -> getUserInputDefaultNo getUserInput message

    let reconcileUserErrors
        (log : ILogger)
        (getUserInput : unit -> string)
        (client : Gitea.Client)
        (m : Map<User, AlignmentError<UserInfo>>)
        =
        let userInputLock = obj ()

        m
        |> Map.toSeq
        |> Seq.map (fun (User user, err) ->
            match err with
            | AlignmentError.DoesNotExist desired ->
                async {
                    let rand = Random ()

                    let pwd =
                        Array.init 15 (fun _ -> rand.Next (65, 65 + 25) |> byte)
                        |> System.Text.Encoding.ASCII.GetString

                    let options = Gitea.CreateUserOption ()
                    options.Email <- desired.Email
                    options.Username <- user
                    options.FullName <- user

                    options.Visibility <-
                        match desired.Visibility with
                        | None -> "public"
                        | Some v -> v

                    options.LoginName <- user
                    options.MustChangePassword <- Some true
                    options.Password <- pwd
                    let! _ = client.AdminCreateUser options |> Async.AwaitTask

                    lock
                        userInputLock
                        (fun () ->
                            log.LogCritical (
                                "Created user {User} with password {Password}, which you must now change",
                                user,
                                pwd
                            )
                        )

                    return ()
                }
            | AlignmentError.UnexpectedlyPresent ->
                async {
                    lock
                        userInputLock
                        (fun () ->
                            let answer =
                                getUserInputDefaultNo getUserInput $"User %s{user} unexpectedly present. Remove?"

                            if answer then
                                client.AdminDeleteUser(user).Result
                            else
                                log.LogCritical ("Refusing to delete user {User}, who is unexpectedly present.", user)
                        )
                }
            | AlignmentError.ConfigurationDiffers (desired, actual) ->
                let updates = UserInfo.Resolve desired actual

                async {
                    lock
                        userInputLock
                        (fun () ->
                            let body = Gitea.EditUserOption ()

                            for update in updates do
                                match update with
                                | UserInfoUpdate.Admin (desired, _) -> body.Admin <- desired
                                | UserInfoUpdate.Email (desired, _) -> body.Email <- desired
                                | UserInfoUpdate.Visibility (desired, _) -> body.Visibility <- desired
                                | UserInfoUpdate.Website (desired, actual) ->
                                    // Per https://github.com/go-gitea/gitea/issues/17126,
                                    // the website parameter can't currently be edited.
                                    // This is a bug that is unlikely to be fixed.
                                    let actual =
                                        match actual with
                                        | None -> "<no website>"
                                        | Some uri -> uri.ToString ()

                                    log.LogCritical (
                                        "User {User} has conflicting website, desired {DesiredWebsite}, existing {ActualWebsite}, which a bug in Gitea means can't be reconciled via the API.",
                                        user,
                                        desired,
                                        actual
                                    )

                            body.LoginName <- user
                            client.AdminEditUser(user, body).Result |> ignore
                        )
                }
        )
        |> Async.Parallel
        |> fun a -> async.Bind (a, Array.iter id >> async.Return)

    [<EntryPoint>]
    let main argv =
        let parser = ArgumentParser.Create<ArgsFragments> ()
        let parsed = parser.Parse argv

        let args =
            {
                ConfigFile = parsed.GetResult ArgsFragments.Config_File |> FileInfo
                GiteaHost = parsed.GetResult ArgsFragments.Gitea_Host |> Uri
                GiteaAdminApiToken = parsed.GetResult ArgsFragments.Gitea_Admin_Api_Token
                GitHubApiToken = parsed.TryGetResult ArgsFragments.GitHub_Api_Token
            }

        let config = GiteaConfig.get args.ConfigFile

        let options =
            let options = ConsoleLoggerOptions ()

            { new IOptionsMonitor<ConsoleLoggerOptions> with
                member _.Get _ = options
                member _.CurrentValue = options

                member _.OnChange _ =
                    { new IDisposable with
                        member _.Dispose () = ()
                    }
            }

        use loggerProvider = new ConsoleLoggerProvider (options)
        let logger = loggerProvider.CreateLogger "Gitea.App"

        use client = new HttpClient ()
        client.BaseAddress <- args.GiteaHost
        client.DefaultRequestHeaders.Add ("Authorization", $"token {args.GiteaAdminApiToken}")

        let client = Gitea.Client client

        task {
            Console.WriteLine "Checking users..."
            let! userErrors = Gitea.checkUsers config client

            match userErrors with
            | Ok () -> ()
            | Error errors -> do! reconcileUserErrors logger Console.ReadLine client errors

            Console.WriteLine "Checking repos..."
            let! repoErrors = Gitea.checkRepos config client

            match repoErrors with
            | Ok () -> ()
            | Error errors -> do! Gitea.reconcileRepoErrors logger client args.GitHubApiToken errors

            match userErrors, repoErrors with
            | Ok (), Ok () -> return 0
            | Ok (), Error _ -> return 1
            | Error _, Ok () -> return 2
            | Error _, Error _ -> return 3
        }
        |> fun t -> t.Result