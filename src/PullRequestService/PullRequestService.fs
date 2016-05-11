﻿(*
 * MindTouch PullRequestService - a DreamService that awaits pull requests
 * notifications from Github and acts upon it
 *
 * Copyright (C) 2006-2013 MindTouch, Inc.
 * www.mindtouch.com  oss@mindtouch.com
 *
 * For community documentation and downloads visit help.mindtouch.us;
 * please review the licensing section.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 *     http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 *)
namespace Mindtouch
open System
open System.Collections.Generic
open Autofac

open MindTouch.Dream
open MindTouch.Tasking
open MindTouch.Xml
open MindTouch.Collections;

open FSharp.Data.Json
open FSharp.Data.Json.Extensions
open Microsoft.FSharp.Collections

open log4net
open MindTouch.Data
exception MissingConfig of string
type Agent<'T> = MailboxProcessor<'T>

[<DreamService(
    "MindTouch Github Pull Request Service",
    "Copyright (C) 2013 MindTouch Inc.",
    SID = [| "sid://mindtouch.com/2013/05/pullrequestservice" |])>]
[<DreamServiceConfig("github.token", "string", "A Github's personal API access token")>]
[<DreamServiceConfig("github.owner", "string", "The owner of the repos we want to watch")>]
[<DreamServiceConfig("github.repos", "string", "Comma separated list of repos to watch")>]
[<DreamServiceConfig("github.frozen.branches", "xml?", "Top level XML for repo/branch nested elements that describe explicitly frozen branches")>]
[<DreamServiceConfig("public.uri", "string", "The notify end-point's full public URI to use to communicate with this service")>]
[<DreamServiceConfig("merge.retries", "int", "The number of times we should retry merging a pull request in case there was an error")>]
[<DreamServiceConfig("merge.ttl", "int", "The amount of time (in milliseconds) that we need to wait before we try to merge the pull request again")>]
[<DreamServiceConfig("mergeability.retries", "int", "The number of times we should retry polling for the mergeability status")>]
[<DreamServiceConfig("mergeability.ttl", "int", "The amount of time (in milliseconds) that we need to wait before we try to check for the mergeability status of the pull request")>]
[<DreamServiceConfig("github2youtrack", "string", "A command separated list of colon separated github and youtrack usernames. For example githubusername1:youtrackUsername1, githubUsername2:youtrackUsername2")>]
[<DreamServiceConfig("youtrack.hostname", "string", "The YouTrack hostname")>]
[<DreamServiceConfig("youtrack.username", "string", "The YouTrack username")>]
[<DreamServiceConfig("youtrack.password", "string", "The YouTrack password")>]
[<DreamServiceConfig("archive.branches.ttl", "int", "How frequently we prune the branches. The value is in milliseconds, it defaults to everyday")>]
[<DreamServiceConfig("archive.branches.keep", "int", "The number of release branches that we need to keep around. Defaults to 4")>]
type PullRequestService() as self =
    inherit DreamService()

    //--- Fields ---
    // Config keys values
    let mutable token = None
    let mutable owner = None
    let mutable frozenBranches = new Dictionary<string, seq<string>>()
    let mutable github2youtrack = Map.empty<string, string>
    let mutable youtrackHostname = None
    let mutable youtrackUsername = None
    let mutable youtrackPassword = None
    let mutable publicUri = None
    let mutable mergeRetries = None
    let mutable mergeabilityRetries = None
    let mutable mergeabilityTTL = TimeSpan.MinValue
    let mutable mergeTTL = TimeSpan.MinValue
    let mutable fromEmail = None
    let mutable toEmail = None
    let emailClient = MindTouch.Email.t()

    // Immutable
    let logger = LogManager.GetLogger typedefof<PullRequestService>
    let timerFactory = TaskTimerFactory.Create(self)
    let branchArchivalExpiringDictionary = new ExpiringDictionary<string, int>(timerFactory, false)

    // Pull Request polling agent
    let pullRequestPollingAgent =
        let pullRequestExpiringCache = new ExpiringDictionary<XUri, int>(timerFactory, false)
        pullRequestExpiringCache.EntryExpired.Add <|
            fun args ->
                let prUri, retry = args.Entry.Key, args.Entry.Value
                if retry < mergeabilityRetries.Value then
                    try
                        let github = MindTouch.Github.t(owner.Value, token.Value)
                        let youtrack = MindTouch.YouTrack.t(youtrackHostname.Value, youtrackUsername.Value, youtrackPassword.Value, github2youtrack)
                        JsonValue.Parse(github.GetPullRequestDetails(prUri).ToText())
                        |> MindTouch.PullRequest.DeterminePullRequestType github.IsReopenedPullRequest youtrack.IssuesValidator youtrack.FilterOutNotExistentIssues (fun repo targetBranch -> frozenBranches.ContainsKey (repo.ToLowerInvariant()) && Seq.exists (fun branch -> targetBranch.EqualsInvariantIgnoreCase(branch)) frozenBranches.[repo.ToLowerInvariant()])
                        |> github.ProcessPullRequestType (fun prUri -> failwith(String.Format("Status for '{0}' is still undetermined", prUri))) (MindTouch.PullRequest.ProcessMergedPullRequest fromEmail.Value toEmail.Value emailClient github youtrack)
                        |> ignore
                    with
                        | :? DreamResponseException as e when e.Response.Status = DreamStatus.MethodNotAllowed || e.Response.Status = DreamStatus.Unauthorized || e.Response.Status = DreamStatus.Forbidden ->
                            raise e
                        | e ->
                            pullRequestExpiringCache.Set(prUri, retry + 1, mergeabilityTTL)
                            logger.Debug(String.Format("Will poll '{0}' status again in '{1}'", prUri, mergeabilityTTL), e)
                            raise e
                else
                    logger.DebugFormat("The maximum number of retries ({1}) for polling status for '{0}' has been reached thus ignored from now on", prUri, mergeabilityRetries.Value)
        
        Agent.Start <| fun inbox ->
            let rec loop (cache : ExpiringDictionary<XUri, int>) = async {
                let! msg = inbox.Receive()
                cache.Set(msg, 0, mergeabilityTTL)
                logger.DebugFormat("Queued '{0}' for status check in '{1}'", msg, mergeabilityTTL)
                return! loop cache
            }
            loop(pullRequestExpiringCache)

    //--- Functions ---
    let ValidateConfig key value =
        match value with
        | None -> raise(MissingConfig key)
        | _ -> ()

    let GetValue value def =
        match value with
        | None -> def
        | Some x -> x

    // NOTE(cesarn): Using the third parameter as the type constraint
    // since 'let' would not let me using constraints in angle brackets
    let GetConfigValue (doc : XDoc) (key : string) (t : 'T) : 'T option =
        let configVal = doc.[key].As<'T>()
        if configVal.HasValue then
            Some configVal.Value
        else
            None

    let GetConfigValueStr (doc : XDoc) (key : string) =
        let configVal = doc.[key].AsText
        if configVal = null then
            None
        else
            Some configVal

    let GetRepoMappings (config : XDoc) (secondLevel : string) : Dictionary<string, seq<string>> =
        let repoMappings = new Dictionary<string, seq<string>>()
        config.["repo"] |> Seq.iter (fun repo ->
            repoMappings.[repo.["@name"].AsText.ToLowerInvariant()] <-
                repo.[secondLevel.ToLowerInvariant()] |> Seq.map (fun user -> user.AsText.ToLowerInvariant()))
        repoMappings

    let GetFrozenBranchesMapping (config : XDoc) : Dictionary<string, seq<string>> =
        GetRepoMappings config "branch"
    
    //--- Methods ---
    override this.Start(config : XDoc, container : ILifetimeScope, result : Result) =
        // NOTE(cesarn): Commented out for now
        // base.Start(config, container, result)

        // Gather
        let config' = GetConfigValueStr config
        token <- config' "github.token"
        owner <- config' "github.owner"
        let repos = config' "github.repos"
        publicUri <- config' "public.uri"
        mergeRetries <- GetConfigValue config "merge.retries" 0
        let mergeTtl = GetConfigValue config "merge.ttl" 0.
        mergeabilityRetries <- GetConfigValue config "mergeability.retries" 0
        let mergeabilityTtl = GetConfigValue config "mergeability.ttl" 0.
        youtrackHostname <- config' "youtrack.hostname"
        youtrackUsername <- config' "youtrack.username"
        youtrackPassword <- config' "youtrack.password"
        let github2youtrackMappingsStr = config' "github2youtrack"
        let archiveBranchesTTL = GetValue (GetConfigValue config "archive.branches.ttl" 0.) (24. * 60. * 60. * 1000.)
        let numberOfBranchesToKeep = GetValue (GetConfigValue config "archive.branches.keep" 0) 10
        fromEmail <- config' "from.email"
        toEmail <- config' "to.email"

        // Validate
        ValidateConfig "github.token" token
        ValidateConfig "github.owner" owner
        ValidateConfig "github.repos" repos
        ValidateConfig "public.uri" publicUri
        ValidateConfig "merge.retries" mergeRetries
        ValidateConfig "merge.ttl" mergeTtl
        ValidateConfig "mergeability.retries" mergeabilityRetries
        ValidateConfig "mergeability.ttl" mergeabilityTtl
        ValidateConfig "github2youtrack" github2youtrackMappingsStr
        ValidateConfig "youtrack.hostname" youtrackHostname
        ValidateConfig "youtrack.username" youtrackUsername
        ValidateConfig "youtrack.password" youtrackPassword
        ValidateConfig "from.email" fromEmail
        ValidateConfig "to.email" toEmail

        // Build dependencies
        mergeTTL <- TimeSpan.FromMilliseconds(mergeTtl.Value)
        mergeabilityTTL <- TimeSpan.FromMilliseconds(mergeabilityTtl.Value)
        frozenBranches <- GetFrozenBranchesMapping config.["github.frozen.branches"]
        let github = MindTouch.Github.t(owner.Value, token.Value)
        
        // Github repos
        let allRepos = repos.Value.Split(',')
       
        // Setup the branch archival process
        branchArchivalExpiringDictionary.EntryExpired.Add <|
            fun args ->
                let entry = args.Entry
                let repo = entry.Key
                let numberOfBranches = entry.Value
                try
                    github.ArchiveBranches repo numberOfBranches |> ignore
                finally
                    branchArchivalExpiringDictionary.Set(repo, numberOfBranches, TimeSpan.FromMilliseconds archiveBranchesTTL)

        allRepos
        |> Seq.iter (fun repo -> branchArchivalExpiringDictionary.Set(repo, numberOfBranchesToKeep, TimeSpan.FromMilliseconds archiveBranchesTTL))

        // Use
        github2youtrack <- github2youtrackMappingsStr.Value.Split(',') 
        |> Seq.map (fun mapping -> mapping.Split(':')) 
        |> Seq.map (fun a -> ((Seq.head a).Trim(), (Seq.last a).Trim()))
        |> Map.ofSeq<string, string>

        github.CreateWebHooks allRepos publicUri.Value
        github.ProcessRepos allRepos (fun prUri -> pullRequestPollingAgent.Post(prUri))
        result.Return()
        Seq.empty<IYield>.GetEnumerator()

    [<DreamFeature("POST:notify", "Receive a pull request notification")>]
    member this.HandleGithubMessage (context : DreamContext) (request : DreamMessage) =
        let githubEvent = request.ToText()
        logger.DebugFormat("Payload: ({0})", githubEvent)
        let github = MindTouch.Github.t(owner.Value, token.Value)
        let youtrack = new MindTouch.YouTrack.t(youtrackHostname.Value, youtrackUsername.Value, youtrackPassword.Value, github2youtrack)
        JsonValue.Parse(githubEvent)
        |> MindTouch.PullRequest.DeterminePullRequestTypeFromEvent github.IsReopenedPullRequest youtrack.IssuesValidator youtrack.FilterOutNotExistentIssues (fun repo targetBranch -> frozenBranches.ContainsKey (repo.ToLowerInvariant()) && Seq.exists (fun branch -> targetBranch.EqualsInvariantIgnoreCase(branch)) frozenBranches.[repo.ToLowerInvariant()])
        |> github.ProcessPullRequestType (fun prUri -> pullRequestPollingAgent.Post(prUri)) ((MindTouch.PullRequest.ProcessMergedPullRequest fromEmail.Value toEmail.Value emailClient github youtrack))

    [<DreamFeature("GET:status", "Check the service's status")>]
    member this.GetStatus (context : DreamContext) (request : DreamMessage) =
        DreamMessage.Ok(MimeType.JSON, JsonValue.String("Running ...").ToString())
