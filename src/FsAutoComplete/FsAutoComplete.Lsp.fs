module FsAutoComplete.Lsp

open Argu
open System
open LanguageServerProtocol.Server
open LanguageServerProtocol.Types
open FsAutoComplete.Utils
open FSharp.Compiler.SourceCodeServices
open LanguageServerProtocol
open LanguageServerProtocol.LspResult
open FsAutoComplete
open FSharpLint.Application.LintWarning
open Newtonsoft.Json.Linq
open LspHelpers
module FcsRange = FSharp.Compiler.Range

type FSharpLspClient(sendServerRequest: ClientNotificationSender) =
    inherit LspClient ()

    override __.WindowShowMessage(p) =
        sendServerRequest "window/showMessage" (box p) |> Async.Ignore

    override __.WindowLogMessage(p) =
        sendServerRequest "window/logMessage" (box p) |> Async.Ignore

    override __.TextDocumentPublishDiagnostics(p) =
        sendServerRequest "textDocument/publishDiagnostics" (box p) |> Async.Ignore

    ///Custom notification for workspace/solution/project loading events
    member __.NotifyWorkspace (p: PlainNotification) =
        sendServerRequest "fsharp/notifyWorkspace" (box p) |> Async.Ignore

    ///Custom notification for initial workspace peek
    member __.NotifyWorkspacePeek (p: PlainNotification) =
        sendServerRequest "fsharp/notifyWorkspacePeek" (box p) |> Async.Ignore

    member __.NotifyCancelledRequest (p: PlainNotification) =
        sendServerRequest "fsharp/notifyCancel" (box p) |> Async.Ignore

    member __.NotifyFileParsed (p: PlainNotification) =
        sendServerRequest "fsharp/fileParsed" (box p) |> Async.Ignore

    // TODO: Add the missing notifications
    // TODO: Implement requests

type FsharpLspServer(commands: Commands, lspClient: FSharpLspClient) =
    inherit LspServer()

    let mutable clientCapabilities: ClientCapabilities option = None
    let mutable glyphToCompletionKind = glyphToCompletionKindGenerator None
    let mutable glyphToSymbolKind = glyphToSymbolKindGenerator None
    let subscriptions = ResizeArray<IDisposable>()

    let mutable config = FSharpConfig.Default

    /// centralize any state changes when the config is updated here
    let updateConfig (newConfig: FSharpConfig) =
        config <- newConfig
        commands.SetDotnetSDKRoot config.DotNetRoot

    //TODO: Thread safe version
    let fixes = System.Collections.Generic.Dictionary<DocumentUri, (LanguageServerProtocol.Types.Range * TextEdit) list>()

    let parseFile (p: DidChangeTextDocumentParams) =

        async {
            if not commands.IsWorkspaceReady then
                Debug.print "[LSP] ParseFile - Workspace not ready"
                ()
            else
                let doc = p.TextDocument
                let filePath = doc.GetFilePath()
                let contentChange = p.ContentChanges |> Seq.tryLast
                match contentChange, doc.Version with
                | Some contentChange, Some version ->
                    if contentChange.Range.IsNone && contentChange.RangeLength.IsNone then
                        let content = contentChange.Text.Split('\n')
                        let tfmConfig = config.UseSdkScripts
                        do! (commands.Parse filePath content version (Some tfmConfig) |> Async.Ignore)

                        if config.Linter then do! (commands.Lint filePath |> Async.Ignore)
                        if config.UnusedOpensAnalyzer then do! (commands.GetUnusedOpens filePath |> Async.Ignore)
                        if config.UnusedDeclarationsAnalyzer then do! (commands.GetUnusedDeclarations filePath |> Async.Ignore)
                        if config.SimplifyNameAnalyzer then do! (commands.GetSimplifiedNames filePath |> Async.Ignore)
                    else
                        Debug.print "[LSP] ParseFile - Parse not started, received partial change"
                | _ ->
                    Debug.print "[LSP] ParseFile - Found no change for %s" filePath
                    ()
        } |> Async.Start

    let parseFileDebuncer = Debounce(500, parseFile)

    let diagnosticCollections = System.Collections.Concurrent.ConcurrentDictionary<DocumentUri * string,Diagnostic[]>()

    let sendDiagnostics (uri: DocumentUri) =
        let diags =
            diagnosticCollections
            |> Seq.collect (fun kv ->
                let (u, _) = kv.Key
                if u = uri then kv.Value else [||])
            |> Seq.sortBy (fun n ->
                n.Range.Start.Line
            )
            |> Seq.toArray
        {Uri = uri; Diagnostics = diags}
        |> lspClient.TextDocumentPublishDiagnostics
        |> Async.Start

    /// convert structure scopes to known kinds of folding range.
    /// this lets commands like 'fold all comments' work sensibly.
    /// impl note: implemented as an exhaustive match here so that
    /// if new structure kinds appear we have to handle them.
    let scopeToKind (scope: Structure.Scope): string option =
        match scope with
        | Structure.Scope.Open -> Some FoldingRangeKind.Imports
        | Structure.Scope.Comment
        | Structure.Scope.XmlDocComment -> Some FoldingRangeKind.Comment
        | Structure.Scope.Namespace
        | Structure.Scope.Module
        | Structure.Scope.Type
        | Structure.Scope.Member
        | Structure.Scope.LetOrUse
        | Structure.Scope.Val
        | Structure.Scope.CompExpr
        | Structure.Scope.IfThenElse
        | Structure.Scope.ThenInIfThenElse
        | Structure.Scope.ElseInIfThenElse
        | Structure.Scope.TryWith
        | Structure.Scope.TryInTryWith
        | Structure.Scope.WithInTryWith
        | Structure.Scope.TryFinally
        | Structure.Scope.TryInTryFinally
        | Structure.Scope.FinallyInTryFinally
        | Structure.Scope.ArrayOrList
        | Structure.Scope.ObjExpr
        | Structure.Scope.For
        | Structure.Scope.While
        | Structure.Scope.Match
        | Structure.Scope.MatchBang
        | Structure.Scope.MatchLambda
        | Structure.Scope.MatchClause
        | Structure.Scope.Lambda
        | Structure.Scope.CompExprInternal
        | Structure.Scope.Quote
        | Structure.Scope.Record
        | Structure.Scope.SpecialFunc
        | Structure.Scope.Do
        | Structure.Scope.New
        | Structure.Scope.Attribute
        | Structure.Scope.Interface
        | Structure.Scope.HashDirective
        | Structure.Scope.LetOrUseBang
        | Structure.Scope.TypeExtension
        | Structure.Scope.YieldOrReturn
        | Structure.Scope.YieldOrReturnBang
        | Structure.Scope.Tuple
        | Structure.Scope.UnionCase
        | Structure.Scope.EnumCase
        | Structure.Scope.RecordField
        | Structure.Scope.RecordDefn
        | Structure.Scope.UnionDefn -> None

    let toFoldingRange (item: Structure.ScopeRange): FoldingRange =
        let kind = scopeToKind item.Scope
        // map the collapserange to the foldingRange
        let lsp = fcsRangeToLsp item.CollapseRange
        { StartCharacter   = Some lsp.Start.Character
          StartLine        = lsp.Start.Line
          EndCharacter     = Some lsp.End.Character
          EndLine          = lsp.End.Line
          Kind             = kind }

    do
        commands.Notify.Subscribe(fun n ->
            try
                Debug.print "[LSP] Notify - %A" n
                match n with
                | NotificationEvent.FileParsed fn ->
                    {Content = fn}
                    |> lspClient.NotifyFileParsed
                    |> Async.Start
                | NotificationEvent.Workspace ws ->
                    let ws = CommandResponse.serialize JsonSerializer.writeJson ws

                    {Content = ws}
                    |> lspClient.NotifyWorkspace
                    |> Async.Start

                | NotificationEvent.ParseError (CoreResponse.Errors (errors, file)) ->
                    let uri = filePathToUri file
                    diagnosticCollections.AddOrUpdate((uri, "F# Compiler"), [||], fun _ _ -> [||]) |> ignore

                    let diags = errors |> Array.map (fcsErrorToDiagnostic)
                    diagnosticCollections.AddOrUpdate((uri, "F# Compiler"), diags, fun _ _ -> diags) |> ignore
                    sendDiagnostics uri

                | NotificationEvent.UnusedOpens (CoreResponse.UnusedOpens (file, opens)) ->
                    let uri = filePathToUri file
                    diagnosticCollections.AddOrUpdate((uri, "F# Unused opens"), [||], fun _ _ -> [||]) |> ignore

                    let diags = opens |> Array.map(fun n ->
                        {Diagnostic.Range = fcsRangeToLsp n; Code = None; Severity = Some DiagnosticSeverity.Hint; Source = "FSAC"; Message = "Unused open statement"; RelatedInformation = Some [||]; Tags = Some [| DiagnosticTag.Unnecessary |] }
                    )
                    diagnosticCollections.AddOrUpdate((uri, "F# Unused opens"), diags, fun _ _ -> diags) |> ignore
                    sendDiagnostics uri

                | NotificationEvent.UnusedDeclarations (CoreResponse.UnusedDeclarations (file, decls)) ->
                    let uri = filePathToUri file
                    diagnosticCollections.AddOrUpdate((uri, "F# Unused declarations"), [||], fun _ _ -> [||]) |> ignore

                    let diags = decls |> Array.map(fun (n, t) ->
                        {Diagnostic.Range = fcsRangeToLsp n; Code = (if t then Some "1" else None); Severity = Some DiagnosticSeverity.Hint; Source = "FSAC"; Message = "This value is unused"; RelatedInformation = Some [||]; Tags = Some [| DiagnosticTag.Unnecessary |] }
                    )
                    diagnosticCollections.AddOrUpdate((uri, "F# Unused declarations"), diags, fun _ _ -> diags) |> ignore
                    sendDiagnostics uri

                | NotificationEvent.SimplifyNames (CoreResponse.SimplifiedName (file, decls)) ->
                    let uri = filePathToUri file
                    diagnosticCollections.AddOrUpdate((uri, "F# simplify names"), [||], fun _ _ -> [||]) |> ignore

                    let diags = decls |> Array.map(fun (n, _) ->
                        {Diagnostic.Range = fcsRangeToLsp n; Code = None; Severity = Some DiagnosticSeverity.Hint; Source = "FSAC"; Message = "This qualifier is redundant"; RelatedInformation = Some [||]; Tags = Some [| DiagnosticTag.Unnecessary |] }
                    )
                    diagnosticCollections.AddOrUpdate((uri, "F# simplify names"), diags, fun _ _ -> diags) |> ignore
                    sendDiagnostics uri

                | NotificationEvent.Lint (CoreResponse.Lint (file, warnings)) ->
                    let uri = filePathToUri file
                    diagnosticCollections.AddOrUpdate((uri, "F# Linter"), [||], fun _ _ -> [||]) |> ignore

                    let fs =
                        warnings |> List.choose (fun w ->
                            w.Warning.Fix
                            |> Option.map (fun f ->
                                let range = fcsRangeToLsp w.Warning.Range
                                range, {Range = range; NewText = f.ToText})
                        )

                    fixes.[uri] <- fs
                    let diags =
                        warnings |> List.map(fun w ->
                            // ideally we'd be able to include a clickable link to the docs page for this errorlint code, but that is not the case here
                            // neither the Message or the RelatedInformation structures support markdown.
                            let range = fcsRangeToLsp w.Warning.Range
                            { Diagnostic.Range = range
                              Code = w.Code |> Option.map (sprintf "FS%04d") // '04' says to pad with '0' up to '4' digits in width. (we're recreating the F#Lint display numbers here)
                              Severity = Some DiagnosticSeverity.Information
                              Source = "F# Linter"
                              Message = w.Warning.Info
                              RelatedInformation = None
                              Tags = None }
                        )
                        |> List.toArray
                    diagnosticCollections.AddOrUpdate((uri, "F# Linter"), diags, fun _ _ -> diags) |> ignore
                    sendDiagnostics uri
                | NotificationEvent.Canceled (CoreResponse.InfoRes msg) ->
                    let ntf = {Content = msg}
                    lspClient.NotifyCancelledRequest ntf
                    |> Async.Start
                | NotificationEvent.Diagnostics(p) ->
                    p
                    |> lspClient.TextDocumentPublishDiagnostics
                    |> Async.Start
                | _ ->
                    //TODO: Add analyzer support
                    ()
            with
            | _ -> ()
        ) |> subscriptions.Add

    ///Helper function for handling Position requests using **recent** type check results
    member x.positionHandler<'a, 'b when 'b :> ITextDocumentPositionParams> (f: 'b -> FcsRange.pos -> ParseAndCheckResults -> string -> string [] ->  AsyncLspResult<'a>) (arg: 'b) : AsyncLspResult<'a> =
        async {
            let pos = arg.GetFcsPos()
            let file = arg.GetFilePath()
            Debug.print "[LSP] PositionHandler - Position request: %s at %A" file pos

            return!
                match commands.TryGetFileCheckerOptionsWithLinesAndLineStr(file, pos) with
                | ResultOrString.Error s ->
                    Debug.print "[LSP] PositionHandler - Getting file checker options failed: %s" s
                    AsyncLspResult.internalError s
                | ResultOrString.Ok (options, lines, lineStr) ->
                    try
                        let tyResOpt = commands.TryGetRecentTypeCheckResultsForFile(file, options)
                        match tyResOpt with
                        | None ->
                            Debug.print "[LSP] PositionHandler - Cached typecheck results not yet available"
                            AsyncLspResult.internalError "Cached typecheck results not yet available"
                        | Some tyRes ->
                            async {
                                let! r = Async.Catch (f arg pos tyRes lineStr lines)
                                match r with
                                | Choice1Of2 r -> return r
                                | Choice2Of2 e ->
                                    Debug.print "[LSP] PositionHandler - Operation failed: %s" e.Message
                                    return LspResult.internalError e.Message
                            }
                    with e ->
                        Debug.print "[LSP] PositionHandler - Operation failed: %s" e.Message
                        AsyncLspResult.internalError e.Message
        }

    ///Helper function for handling Position requests using **latest** type check results
    member x.positionHandlerWithLatest<'a, 'b when 'b :> ITextDocumentPositionParams> (f: 'b -> FcsRange.pos -> ParseAndCheckResults -> string -> string [] ->  AsyncLspResult<'a>) (arg: 'b) : AsyncLspResult<'a> =
        async {
            let pos = arg.GetFcsPos()
            let file = arg.GetFilePath()
            Debug.print "[LSP] PositionHandler - Position request: %s at %A" file pos

            return!
                    try
                        async {
                        let! tyResOpt = commands.TryGetLatestTypeCheckResultsForFile(file)
                        return!
                            match tyResOpt with
                            | None ->
                                Debug.print "[LSP] PositionHandler - Cached typecheck results not yet available"
                                AsyncLspResult.internalError "Cached typecheck results not yet available"
                            | Some tyRes ->
                                match commands.TryGetFileCheckerOptionsWithLinesAndLineStr(file, pos) with
                                | ResultOrString.Error s ->
                                    Debug.print "[LSP] PositionHandler - Getting file checker options failed: %s" s
                                    AsyncLspResult.internalError s
                                | ResultOrString.Ok (options, lines, lineStr) ->
                                    async {
                                        let! r = Async.Catch (f arg pos tyRes lineStr lines)
                                        match r with
                                        | Choice1Of2 r -> return r
                                        | Choice2Of2 e ->
                                            Debug.print "[LSP] PositionHandler - Operation failed: %s" e.Message
                                            return LspResult.internalError e.Message
                                    }
                        }
                    with e ->
                        Debug.print "[LSP] PositionHandler - Operation failed: %s" e.Message
                        AsyncLspResult.internalError e.Message
        }


    override __.Initialize(p) = async {
        Debug.print "[LSP call] Initialize"
        commands.StartBackgroundService p.RootPath
        clientCapabilities <- p.Capabilities
        glyphToCompletionKind <- glyphToCompletionKindGenerator clientCapabilities
        glyphToSymbolKind <- glyphToSymbolKindGenerator clientCapabilities

        let c =
            p.InitializationOptions
            |> Option.bind (fun options -> if options.HasValues then Some options else None)
            |> Option.map Server.deserialize<FSharpConfigDto>
            |> Option.map FSharpConfig.FromDto
            |> Option.getOrElse FSharpConfig.Default

        updateConfig c

        // Debug.print "Config: %A" c

        match p.RootPath, c.AutomaticWorkspaceInit with
        | None, _
        | _, false -> ()
        | Some p, true ->
            async {
                let! peek = commands.WorkspacePeek p config.WorkspaceModePeekDeepLevel (List.ofArray config.ExcludeProjectDirectories)

                match peek.[0] with
                | CoreResponse.InfoRes msg | CoreResponse.ErrorRes msg ->
                    ()
                | CoreResponse.WorkspacePeek ints ->

                    let serialized = CommandResponse.workspacePeek JsonSerializer.writeJson ints
                    lspClient.NotifyWorkspacePeek {Content = serialized} |> Async.Start

                    let peeks =
                        ints
                        |> List.map Workspace.mapInteresting
                        |> List.sortByDescending (fun x ->
                            match x with
                            | CommandResponse.WorkspacePeekFound.Solution sln -> Workspace.countProjectsInSln sln
                            | CommandResponse.WorkspacePeekFound.Directory _ -> -1)

                    match peeks with
                    | [] -> ()
                    | [CommandResponse.WorkspacePeekFound.Directory projs] ->
                        commands.WorkspaceLoad ignore projs.Fsprojs false config.ScriptTFM
                        |> Async.Ignore
                        |> Async.Start
                    | CommandResponse.WorkspacePeekFound.Solution sln::_ ->
                        let projs =
                            sln.Items
                            |> List.collect Workspace.foldFsproj
                            |> List.map fst
                        commands.WorkspaceLoad ignore projs false config.ScriptTFM
                        |> Async.Ignore
                        |> Async.Start
                    | _ ->
                        //TODO: Above case always picks solution with most projects, should be changed
                        ()
                | _ -> ()


                return ()
            } |> Async.Start

        // Debug.print "INIT RETURN"
        return
            { InitializeResult.Default with
                Capabilities =
                    { ServerCapabilities.Default with
                        HoverProvider = Some true
                        RenameProvider = Some true
                        DefinitionProvider = Some true
                        TypeDefinitionProvider = Some true
                        ImplementationProvider = Some true
                        ReferencesProvider = Some true
                        DocumentHighlightProvider = Some true
                        DocumentSymbolProvider = Some true
                        WorkspaceSymbolProvider = Some true
                        DocumentFormattingProvider = Some true
                        DocumentRangeFormattingProvider = Some false
                        SignatureHelpProvider = Some {
                            SignatureHelpOptions.TriggerCharacters = Some [| "("; ","|]
                        }
                        CompletionProvider =
                            Some {
                                ResolveProvider = Some true
                                TriggerCharacters = Some ([| "."; "'"; |])
                            }
                        CodeLensProvider = Some {
                            CodeLensOptions.ResolveProvider = Some true
                        }
                        CodeActionProvider = Some true
                        TextDocumentSync =
                            Some { TextDocumentSyncOptions.Default with
                                     OpenClose = Some true
                                     Change = Some TextDocumentSyncKind.Full
                                     Save = Some { IncludeText = Some true }
                                 }
                        FoldingRangeProvider = Some true
                    }
            }
            |> success
    }

    override __.Initialized(p) = async {
        Debug.print "[LSP call] Initialized"
        return ()
    }

    override __.TextDocumentDidOpen(p) = async {
        Debug.print "[LSP call] TextDocumentDidOpen"
        let doc = p.TextDocument
        let filePath = doc. GetFilePath()
        let content = doc.Text.Split('\n')
        let tfmConfig = config.UseSdkScripts

        commands.SetFileContent(filePath, content, Some doc.Version, config.ScriptTFM)


        if not commands.IsWorkspaceReady then
            do! commands.WorkspaceReady |> Async.AwaitEvent
            Debug.print "[LSP call] TextDocumentDidOpen - workspace ready"

        do! (commands.Parse filePath content doc.Version (Some tfmConfig) |> Async.Ignore)

        if config.Linter then do! (commands.Lint filePath |> Async.Ignore)
        if config.UnusedOpensAnalyzer then do! (commands.GetUnusedOpens filePath |> Async.Ignore)
        if config.UnusedDeclarationsAnalyzer then do! (commands.GetUnusedDeclarations filePath |> Async.Ignore)
        if config.SimplifyNameAnalyzer then do! (commands.GetSimplifiedNames filePath |> Async.Ignore)
    }

    override __.TextDocumentDidChange(p) = async {
        Debug.print "[LSP call] TextDocumentDidChange"
        let doc = p.TextDocument
        let filePath = doc.GetFilePath()
        let contentChange = p.ContentChanges |> Seq.tryLast
        match contentChange, doc.Version with
        | Some contentChange, Some version ->
            if contentChange.Range.IsNone && contentChange.RangeLength.IsNone then
                let content = contentChange.Text.Split('\n')
                commands.SetFileContent(filePath, content, Some version, config.ScriptTFM)
            else ()
        | _ -> ()

        parseFileDebuncer.Bounce p
    }

    //TODO: Investigate if this should be done at all
    override __.TextDocumentDidSave(p) = async {
        Debug.print "[LSP call] TextDocumentDidSave"
        if not commands.IsWorkspaceReady then
            Debug.print "[LSP] DidSave - Workspace not ready"
        else
            let doc = p.TextDocument
            let filePath = doc.GetFilePath()

            //Parsing projects on file save puts too much pressure on CPU -
            //even if it isn't blocking main functionalities due to being in background process
            //just plain CPU and memory usage is probably too high to enable this at all
            //Investigate more.

            //commands.ProcessProjectsInBackground filePath
            ()
    }

    override __.TextDocumentCompletion(p) = async {
        Debug.print "[LSP call] TextDocumentCompletion"
        Debug.print "[LSP call] TextDocumentCompletion - context: %A" p.Context
        // Sublime-lsp doesn't like when we answer null so we answer an empty list instead
        let noCompletion = success (Some { IsIncomplete = true; Items = [||] })
        let doc = p.TextDocument
        let file = doc.GetFilePath()
        let pos = p.GetFcsPos()
        let! res =
            match commands.TryGetFileCheckerOptionsWithLines file with
            | ResultOrString.Error s -> AsyncLspResult.internalError s
            | ResultOrString.Ok (options, lines) ->
                let line = p.Position.Line
                let col = p.Position.Character
                let lineStr = lines.[line]
                let word = lineStr.Substring(0, col)
                let ok = line <= lines.Length && line >= 0 && col <= lineStr.Length + 1 && col >= 0
                if not ok then
                    AsyncLspResult.internalError "not ok"
                elif (lineStr.StartsWith "#" && (FsAutoComplete.KeywordList.hashDirectives |> List.exists (fun (n,_) -> n.StartsWith word ) || word.Contains "\n" )) then
                    let its =
                        FsAutoComplete.KeywordList.hashDirectives
                        |> List.map (fun (k, d) ->
                            { CompletionItem.Create(k) with
                                Kind = Some CompletionItemKind.Keyword
                                InsertText = Some k
                                FilterText = Some k
                                SortText = Some k
                                Documentation = Some (Documentation.String d)
                                Label = "#" + k
                            })
                        |> List.toArray
                    let completionList = { IsIncomplete = false; Items = its}
                    async.Return (success (Some completionList))
                else
                    async {
                        let! tyResOpt =
                            match p.Context with
                            | None -> commands.TryGetRecentTypeCheckResultsForFile(file, options) |> async.Return
                            | Some ctx ->
                                //ctx.triggerKind = CompletionTriggerKind.Invoked ||
                                if  (ctx.triggerCharacter = Some ".") then
                                    commands.TryGetLatestTypeCheckResultsForFile(file)
                                else
                                    commands.TryGetRecentTypeCheckResultsForFile(file, options) |> async.Return

                        match tyResOpt with
                        | None -> return LspResult.internalError "no type check results"
                        | Some tyRes ->
                            let! res = commands.Completion tyRes pos lineStr lines file None (config.KeywordsAutocomplete) (config.ExternalAutocomplete)
                            let x = if res.Length = 1 then res.[0] else res.[1]
                            let res =
                                match x with
                                | CoreResponse.Completion(decls, keywords) ->
                                    let items =
                                        decls
                                        |> Array.mapi (fun id d ->
                                            let code =
                                                if System.Text.RegularExpressions.Regex.IsMatch(d.Name, """^[a-zA-Z][a-zA-Z0-9']+$""") then d.Name
                                                elif d.NamespaceToOpen.IsSome then d.Name
                                                else PrettyNaming.QuoteIdentifierIfNeeded d.Name
                                            let label =
                                                match d.NamespaceToOpen with
                                                | Some no -> sprintf "%s (open %s)" d.Name no
                                                | None -> d.Name

                                            { CompletionItem.Create(d.Name) with
                                                Kind = glyphToCompletionKind d.Glyph
                                                InsertText = Some code
                                                SortText = Some (sprintf "%06d" id)
                                                FilterText = Some d.Name
                                                Label = label
                                            }
                                        )
                                    let kwds =
                                        if not keywords
                                        then []
                                        else
                                            FsAutoComplete.KeywordList.allKeywords
                                            |> List.mapi (fun id k ->
                                                { CompletionItem.Create(k) with
                                                    Kind = Some CompletionItemKind.Keyword
                                                    InsertText = Some k
                                                    SortText = Some (sprintf "1000000%d" id)
                                                    FilterText = Some k
                                                    Label = k })
                                    let its = Array.append items (List.toArray kwds)
                                    let completionList = { IsIncomplete = false; Items = its}
                                    success (Some completionList)
                                | _ -> noCompletion
                            return res
                    }
        return res
    }

    override __.CompletionItemResolve(ci) = async {
        Debug.print "[LSP call] CompletionItemResolve"
        let res = commands.Helptext ci.InsertText.Value
        let res =
            match res.[0] with
            | CoreResponse.InfoRes msg | CoreResponse.ErrorRes msg ->
                ci
            | CoreResponse.HelpTextSimple(name, str) ->
                let d = Documentation.Markup (markdown str)
                {ci with Detail = Some name; Documentation = Some d  }
            | CoreResponse.HelpText (name, tip, additionalEdit) ->
                let (si, comment) = (TipFormatter.formatTip tip) |> List.collect id |> List.head
                //TODO: Add insert namespace
                let d = Documentation.Markup (markdown comment)
                {ci with Detail = Some si; Documentation = Some d  }
            | _ -> ci
        return success res
    }

    override x.TextDocumentSignatureHelp(p) =
        Debug.print "[LSP call] TextDocumentSignatureHelp"
        p |> x.positionHandlerWithLatest (fun p pos tyRes lineStr lines ->
            async {
                let! res = commands.Methods tyRes  pos lines
                let res =
                    match res.[0] with
                    | CoreResponse.InfoRes msg | CoreResponse.ErrorRes msg ->
                        LspResult.internalError msg
                    | CoreResponse.Methods (methods, commas) ->
                        let sigs =
                            methods.Methods |> Array.map(fun m ->
                                let (sign, comm) = TipFormatter.formatTip m.Description |> List.head |> List.head
                                let parameters =
                                    m.Parameters |> Array.map (fun p ->
                                        {ParameterInformation.Label = p.ParameterName; Documentation = Some (Documentation.String p.CanonicalTypeTextForSorting)}
                                    )
                                let d = Documentation.Markup (markdown comm)
                                { SignatureInformation.Label = sign; Documentation = Some d; Parameters = Some parameters }
                            )

                        let activSig =
                            let sigs = sigs |> Seq.sortBy (fun n -> n.Parameters.Value.Length)
                            sigs
                            |> Seq.findIndex (fun s -> s.Parameters.Value.Length >= commas)
                            |> fun index -> if index + 1 >= (sigs |> Seq.length) then index else index + 1

                        let res = {Signatures = sigs;
                                   ActiveSignature = Some activSig;
                                   ActiveParameter = Some commas }



                        success (Some res)
                    | _ -> LspResult.notImplemented


                return res
            }
        )

    override x.TextDocumentHover(p) =
        Debug.print "[LSP call] TextDocumentHover"
        p |> x.positionHandler (fun p pos tyRes lineStr lines ->
            async {
                let! res = commands.ToolTip tyRes pos lineStr
                let res =
                    match res.[0] with
                    | CoreResponse.InfoRes msg | CoreResponse.ErrorRes msg ->
                        LspResult.internalError msg
                    | CoreResponse.ToolTip(tip, signature, footer, typeDoc) ->
                        let (signature, comment, footer) = TipFormatter.formatTipEnhanced tip signature footer typeDoc |> List.head |> List.head //I wonder why we do that

                        let markStr lang (value:string) = MarkedString.WithLanguage { Language = lang ; Value = value }
                        let fsharpBlock (lines: string[]) = lines |> String.concat "\n" |> markStr "fsharp"

                        let sigContent =
                            let lines =
                                signature.Split '\n'
                                |> Array.filter (not << String.IsNullOrWhiteSpace)

                            match lines |> Array.splitAt (lines.Length - 1) with
                            | (h, [| StartsWith "Full name:" fullName |]) ->
                                [| yield fsharpBlock h
                                   yield MarkedString.String ("*" + fullName + "*") |]
                            | _ -> [| fsharpBlock lines |]


                        let commentContent =
                            comment
                            |> Markdown.createCommentBlock
                            |> MarkedString.String

                        let footerContent =
                            footer.Split '\n'
                            |> Array.filter (not << String.IsNullOrWhiteSpace)
                            |> Array.map (fun n -> MarkedString.String ("*" + n + "*"))


                        let response =
                            {
                                Contents =
                                    MarkedStrings
                                        [|
                                            yield! sigContent
                                            yield commentContent
                                            yield! footerContent
                                        |]
                                Range = None
                            }
                        success (Some response)
                    | _ -> LspResult.notImplemented
                return res
            })

    override x.TextDocumentRename(p) =
        Debug.print "[LSP call] TextDocumentRename"
        p |> x.positionHandler (fun p pos tyRes lineStr lines ->
            async {
                let! res = commands.SymbolUseProject tyRes pos lineStr
                let res =
                    match res.[0] with
                    | CoreResponse.InfoRes msg | CoreResponse.ErrorRes msg ->
                        LspResult.internalError msg
                    | CoreResponse.SymbolUse (_, uses) ->
                        let documentChanges =
                            uses
                            |> Array.groupBy (fun sym -> sym.FileName)
                            |> Array.map(fun (fileName, symbols) ->
                                let edits =
                                    symbols
                                    |> Array.map (fun sym ->
                                        let range = fcsRangeToLsp sym.RangeAlternate
                                        let range = {range with Start = { Line = range.Start.Line; Character = range.End.Character - sym.Symbol.DisplayName.Length }}
                                        {
                                            Range = range
                                            NewText = p.NewName
                                        })
                                    |> Array.distinct
                                {
                                    TextDocument =
                                        {
                                            Uri = filePathToUri fileName
                                            Version = commands.TryGetFileVersion fileName
                                        }
                                    Edits = edits
                                }
                            )
                        WorkspaceEdit.Create(documentChanges, clientCapabilities.Value) |> Some |> success
                    | CoreResponse.SymbolUseRange uses ->
                        let documentChanges =
                            uses
                            |> Array.groupBy (fun sym -> sym.FileName)
                            |> Array.map(fun (fileName, symbols) ->
                                let edits =
                                    symbols
                                    |> Array.map (fun sym ->
                                        let range = symbolUseRangeToLsp sym
                                        let range = {range with Start = { Line = range.Start.Line; Character = range.End.Character - sym.SymbolDisplayName.Length }}

                                        {
                                            Range = range
                                            NewText = p.NewName
                                        })
                                    |> Array.distinct
                                {
                                    TextDocument =
                                        {
                                            Uri = filePathToUri fileName

                                            Version = commands.TryGetFileVersion fileName
                                        }
                                    Edits = edits
                                }
                            )
                        WorkspaceEdit.Create(documentChanges, clientCapabilities.Value) |> Some |> success
                    | _ -> LspResult.notImplemented
                return res
            })

    override x.TextDocumentDefinition(p) =
        Debug.print "[LSP call] TextDocumentDefinition"
        p |> x.positionHandler (fun p pos tyRes lineStr lines ->
            async {
                //TODO: Add #load reference
                let! res = commands.FindDeclaration tyRes pos lineStr
                let res =
                    match res.[0] with
                    | CoreResponse.InfoRes msg | CoreResponse.ErrorRes msg ->
                        LspResult.internalError msg
                    | CoreResponse.FindDeclaration r ->
                        findDeclToLspLocation r
                        |> GotoResult.Single
                        |> Some
                        |> success
                    | _ -> LspResult.notImplemented
                return res
            })

    override x.TextDocumentTypeDefinition(p) =
        Debug.print "[LSP call] TextDocumentTypeDefinition"
        p |> x.positionHandler (fun p pos tyRes lineStr lines ->
            async {
                let! res = commands.FindTypeDeclaration tyRes pos lineStr
                let res =
                    match res.[0] with
                    | CoreResponse.InfoRes msg | CoreResponse.ErrorRes msg ->
                        LspResult.internalError msg
                    | CoreResponse.FindTypeDeclaration r ->
                        fcsRangeToLspLocation r
                        |> GotoResult.Single
                        |> Some
                        |> success
                    | _ -> LspResult.notImplemented
                return res
            })

    override x.TextDocumentReferences(p) =
        Debug.print "[LSP call] TextDocumentRefrences"
        p |> x.positionHandler (fun p pos tyRes lineStr lines ->
            async {
                let! res = commands.SymbolUseProject tyRes pos lineStr
                let res =
                    match res.[0] with
                    | CoreResponse.InfoRes msg | CoreResponse.ErrorRes msg ->
                        LspResult.internalError msg
                    | CoreResponse.SymbolUse (_, uses) ->
                        uses
                        |> Array.map (fun n -> fcsRangeToLspLocation n.RangeAlternate)
                        |> Some
                        |> success
                    | CoreResponse.SymbolUseRange uses ->
                        uses
                        |> Array.map symbolUseRangeToLspLocation
                        |> Some
                        |> success
                    | _ -> LspResult.notImplemented
                return res
            })

    override x.TextDocumentDocumentHighlight(p) =
        Debug.print "[LSP call] TextDocumentDocumentHighlight"
        p |> x.positionHandler (fun p pos tyRes lineStr lines ->
            async {
                let! res = commands.SymbolUse tyRes pos lineStr
                let res =
                    match res.[0] with
                    | CoreResponse.InfoRes msg | CoreResponse.ErrorRes msg ->
                        LspResult.internalError msg
                    | CoreResponse.SymbolUse (symbol, uses) ->
                        uses
                        |> Array.map (fun s ->
                        {
                            DocumentHighlight.Range = fcsRangeToLsp s.RangeAlternate
                            Kind = None
                        })
                        |> Some
                        |> success
                    | _ -> LspResult.notImplemented
                return res
            })

    override x.TextDocumentImplementation(p) =
        Debug.print "[LSP call] TextDocumentImplementation"
        p |> x.positionHandler (fun p pos tyRes lineStr lines ->
            async {
                let! res = commands.SymbolImplementationProject tyRes pos lineStr
                let res =
                    match res.[0] with
                    | CoreResponse.InfoRes msg | CoreResponse.ErrorRes msg ->
                        LspResult.internalError msg
                    | CoreResponse.SymbolUseImplementation (symbol, uses) ->
                        uses
                        |> Array.map (fun n -> fcsRangeToLspLocation n.RangeAlternate)
                        |> GotoResult.Multiple
                        |> Some
                        |> success
                    | CoreResponse.SymbolUseImplementationRange uses ->
                        uses
                        |> Array.map symbolUseRangeToLspLocation
                        |> GotoResult.Multiple
                        |> Some
                        |> success
                    | _ -> LspResult.notImplemented
                return res
            })


    override __.TextDocumentDocumentSymbol(p) = async {
        Debug.print "[LSP call] TextDocumentDocumentSymbol"
        let fn = p.TextDocument.GetFilePath()
        let! res = commands.Declarations fn None (commands.TryGetFileVersion fn)
        let res =
            match res.[0] with
            | CoreResponse.InfoRes msg | CoreResponse.ErrorRes msg ->
                LspResult.internalError msg
            | CoreResponse.Declarations (decls) ->
                decls
                |> Array.map (fst >> getSymbolInformations p.TextDocument.Uri glyphToSymbolKind)
                |> Seq.collect id
                |> Seq.toArray
                |> Some
                |> success
            | _ -> LspResult.notImplemented
        return res
    }

    override __.WorkspaceSymbol(p) = async {
        Debug.print "[LSP call] WorkspaceSymbol"
        let! res = commands.DeclarationsInProjects ()
        let res =
            match res.[0] with
            | CoreResponse.InfoRes msg | CoreResponse.ErrorRes msg ->
                LspResult.internalError msg
            | CoreResponse.Declarations (decls) ->
                decls
                |> Array.map (fun (n,p) ->
                    let uri = filePathToUri p
                    getSymbolInformations uri glyphToSymbolKind n)
                |> Seq.collect id
                |> Seq.filter(fun symbolInfo -> symbolInfo.Name.StartsWith(p.Query))
                |> Seq.toArray
                |> Some
                |> success
            | _ -> LspResult.notImplemented
        return res
    }

    override __.TextDocumentFormatting(p) = async {
        let doc = p.TextDocument
        let fileName = doc.GetFilePath()
        match commands.TryGetFileCheckerOptionsWithLines fileName with
        | Result.Ok (opts, lines) ->
            let range =
                let zero = { Line = 0; Character = 0 }
                let endLine = Array.length lines - 1
                let endCharacter =
                    Array.tryLast lines
                    |> Option.map (fun line -> if line.Length = 0 then 0 else line.Length - 1)
                    |> Option.defaultValue 0
                { Start = zero; End = { Line = endLine; Character = endCharacter } }

            let source = String.concat "\n" lines
            let parsingOptions = Utils.projectOptionsToParseOptions opts
            let checker : FSharpChecker = commands.GetChecker()
            let! formatted =
                Fantomas.CodeFormatter.FormatDocumentAsync(fileName,
                                                           Fantomas.SourceOrigin.SourceString source,
                                                           Fantomas.FormatConfig.FormatConfig.Default,
                                                           parsingOptions,
                                                           checker)

            return LspResult.success(Some([| { Range = range; NewText = formatted  } |]))
        | Result.Error er ->
            return LspResult.notImplemented
    }

    override __.TextDocumentRangeFormatting(p) = async {
        let doc = p.TextDocument
        let fileName = doc.GetFilePath()
        match commands.TryGetFileCheckerOptionsWithLines fileName with
        | Result.Ok (opts, lines) ->
            let range = Fantomas.CodeFormatter.MakeRange(fileName, (p.Range.Start.Line + 1), (p.Range.Start.Character + 1), (p.Range.End.Line + 1), (p.Range.End.Character + 1))

            let source = String.concat "\n" lines
            let parsingOptions = Utils.projectOptionsToParseOptions opts
            let checker : FSharpChecker = commands.GetChecker()
            let! formatted =
                Fantomas.CodeFormatter.FormatSelectionAsync(fileName,
                                                            range,
                                                            Fantomas.SourceOrigin.SourceString source,
                                                            Fantomas.FormatConfig.FormatConfig.Default,
                                                            parsingOptions,
                                                            checker)

            return LspResult.success(Some([| { Range = p.Range; NewText = formatted  } |]))
        | Result.Error er ->
            return LspResult.notImplemented
    }

    member private x.HandleTypeCheckCodeAction file pos f =
        async {
                return!
                    match commands.TryGetFileCheckerOptionsWithLinesAndLineStr(file, pos) with
                    | ResultOrString.Error s ->
                        async.Return []
                    | ResultOrString.Ok (options, lines, lineStr) ->
                        try
                            async {
                                let! tyResOpt = commands.TryGetLatestTypeCheckResultsForFile(file)
                                match tyResOpt with
                                | None ->
                                    return []
                                | Some tyRes ->
                                        let! r = Async.Catch (f tyRes lineStr lines)
                                        match r with
                                        | Choice1Of2 r -> return r
                                        | Choice2Of2 e ->
                                            return []

                            }
                        with e ->
                            async.Return []
            }

    member private __.IfDiagnostic (str: string) handler p =
        let diag =
            p.Context.Diagnostics |> Seq.tryFind (fun n -> n.Message.Contains str)
        match diag with
        | None -> async.Return []
        | Some d -> handler d

    member private __.CreateFix uri fn title (d: Diagnostic option) range replacement  =
        let e =
            {
                Range = range
                NewText = replacement
            }
        let edit =
            {
                TextDocument =
                    {
                        Uri = uri
                        Version = commands.TryGetFileVersion fn
                    }
                Edits = [|e|]
            }
        let we = WorkspaceEdit.Create([|edit|], clientCapabilities.Value)


        { CodeAction.Title = title
          Kind = Some "quickfix"
          Diagnostics = d |> Option.map Array.singleton
          Edit = we
          Command = None}

    member private x.GetUnusedOpensCodeActions fn p =
        if config.UnusedOpensAnalyzer then
            p |> x.IfDiagnostic "Unused open statement" (fun d ->
                let range = {
                    Start = {Line = d.Range.Start.Line - 1; Character = 1000}
                    End = {Line = d.Range.End.Line; Character = d.Range.End.Character}
                }
                let action = x.CreateFix p.TextDocument.Uri fn "Remove unused open" (Some d) range ""
                async.Return [action])
        else
            async.Return []

    member private x.GetErrorSuggestionsCodeActions fn p =
        p |> x.IfDiagnostic "Maybe you want one of the following:" (fun d ->
            d.Message.Split('\n').[1..]
            |> Array.map (fun suggestion ->
                let s = suggestion.Trim()
                let s =
                    if System.Text.RegularExpressions.Regex.IsMatch(s, """^[a-zA-Z][a-zA-Z0-9']+$""") then
                        s
                    else
                        "``" + s + "``"
                let title = sprintf "Replace with %s" s
                let action = x.CreateFix p.TextDocument.Uri fn title (Some d) d.Range s
                action)
            |> Array.toList
            |> async.Return
        )

    member private x.GetNewKeywordSuggestionCodeAction fn p lines =
        p |> x.IfDiagnostic "It is recommended that objects supporting the IDisposable interface are created using the syntax" (fun d ->
            let s = "new " + getText lines d.Range
            x.CreateFix p.TextDocument.Uri fn "Add new" (Some d) d.Range s
            |> List.singleton
            |> async.Return
        )

    member private x.GetUnusedCodeAction fn p lines =
        p |> x.IfDiagnostic "is unused" (fun d ->
            match d.Code with
            | None ->
                let s = "_"
                let s2 = "_" + getText lines d.Range
                [
                    x.CreateFix p.TextDocument.Uri fn "Replace with _" (Some d) d.Range s
                    x.CreateFix p.TextDocument.Uri fn "Prefix with _" (Some d) d.Range s2
                ] |> async.Return
            | Some _ ->
                [
                    x.CreateFix p.TextDocument.Uri fn "Replace with __" (Some d) d.Range "__"
                ] |> async.Return

        )

    member private x.GetRedundantQualfierCodeAction fn p =
        p |> x.IfDiagnostic "This qualifier is redundant" (fun d ->
            [
                x.CreateFix p.TextDocument.Uri fn "Remove redundant qualifier" (Some d) d.Range ""
            ] |> async.Return
        )

    member private x.GetLinterCodeAction fn p =
        p |> x.IfDiagnostic "Lint:" (fun d ->
            let uri = filePathToUri fn

            match fixes.TryGetValue uri with
            | false, _ -> async.Return []
            | true, lst ->
                match lst |> Seq.tryFind (fun (r, te) -> r = d.Range) with
                | None -> async.Return []
                | Some (r, te) ->
                    x.CreateFix p.TextDocument.Uri fn (sprintf "Replace with %s" te.NewText) (Some d) te.Range te.NewText
                    |> List.singleton
                    |> async.Return
        )

    member private x.GetUnionCaseGeneratorCodeAction fn p (lines: string[]) =
        p |> x.IfDiagnostic "Incomplete pattern matches on this expression. For example" (fun d ->
            async {
                if config.UnionCaseStubGeneration then
                    let caseLine = d.Range.Start.Line + 1
                    let col = lines.[caseLine].IndexOf('|') + 3 // Find column of first case in patern matching
                    let pos = FcsRange.mkPos (caseLine + 1) (col + 1) //Must points on first case in 1-based system
                    let! res = x.HandleTypeCheckCodeAction fn pos (fun tyRes line lines -> commands.GetUnionPatternMatchCases tyRes pos lines line)
                    let res =
                        match res.[0] with
                        | CoreResponse.UnionCase (text, position) ->
                            let range = {
                                Start = fcsPosToLsp position
                                End = fcsPosToLsp position
                            }
                            let text = text.Replace("$1", config.UnionCaseStubGenerationBody)
                            [x.CreateFix p.TextDocument.Uri fn "Generate union pattern match case" (Some d) range text ]
                        | _ ->
                            []
                    return res
                else
                    return []
            }
        )

    member private x.GetInterfaceStubCodeAction fn (p: CodeActionParams) (lines: string[]) =
        async {
            if config.InterfaceStubGeneration then
                let pos = protocolPosToPos p.Range.Start
                let! res = x.HandleTypeCheckCodeAction fn pos (fun tyRes line lines -> commands.GetInterfaceStub tyRes pos lines line)
                let res =
                    match res with
                    | CoreResponse.InterfaceStub (text, position)::_ ->
                        let range = {
                            Start = fcsPosToLsp position
                            End = fcsPosToLsp position
                        }
                        let text =
                            text.Replace("$objectIdent", config.InterfaceStubGenerationObjectIdentifier)
                                .Replace("$methodBody", config.InterfaceStubGenerationMethodBody)
                        [x.CreateFix p.TextDocument.Uri fn "Generate interface stubs" None range text ]
                    | _ ->
                        []
                return res
            else
                return []
        }

    member private x.GetRecordStubCodeAction fn (p: CodeActionParams) (lines: string[]) =
        async {
            if config.RecordStubGeneration then
                let pos = protocolPosToPos p.Range.Start
                let! res = x.HandleTypeCheckCodeAction fn pos (fun tyRes line lines -> commands.GetRecordStub tyRes pos lines line)
                let res =
                    match res with
                    | CoreResponse.RecordStub (text, position)::_ ->
                        let range = {
                            Start = fcsPosToLsp position
                            End = fcsPosToLsp position
                        }
                        let text = text.Replace("$1", config.RecordStubGenerationBody)
                        [x.CreateFix p.TextDocument.Uri fn "Generate record stubs" None range text ]
                    | _ ->
                        []
                return res
            else
                return []
        }

    member private x.GetResolveNamespaceActions fn (p: CodeActionParams) =
        let insertLine line lineStr =
            {
                Range = {
                    Start = {Line = line; Character = 0}
                    End = {Line = line; Character = 0}
                }
                NewText = lineStr
            }


        let adjustInsertionPoint (lines: string[]) (ctx : InsertContext) =
            let l = ctx.Pos.Line
            match ctx.ScopeKind with
            | TopModule when l > 1 ->
                let line = lines.[l - 2]
                let isImpliciteTopLevelModule = not (line.StartsWith "module" && not (line.EndsWith "="))
                if isImpliciteTopLevelModule then 1 else l
            | TopModule -> 1
            | ScopeKind.Namespace when l > 1 ->
                [0..l - 1]
                |> List.mapi (fun i line -> i, lines.[line])
                |> List.tryPick (fun (i, lineStr) ->
                    if lineStr.StartsWith "namespace" then Some i
                    else None)
                |> function
                    // move to the next line below "namespace" and convert it to F# 1-based line number
                    | Some line -> line + 2
                    | None -> l
            | ScopeKind.Namespace -> 1
            | _ -> l

        if config.ResolveNamespaces then
            p |> x.IfDiagnostic "is not defined" (fun d ->
                async {
                    let pos = protocolPosToPos d.Range.Start
                    return!
                        x.HandleTypeCheckCodeAction fn pos (fun tyRes line lines ->
                            async {
                                let! res = commands.GetNamespaceSuggestions tyRes pos line
                                let res =
                                    match res.[0] with
                                    | CoreResponse.InfoRes msg | CoreResponse.ErrorRes msg ->
                                        []
                                    | CoreResponse.ResolveNamespaces (word, opens, qualifiers) ->
                                        let quals =
                                            qualifiers
                                            |> List.map (fun (name, qual) ->
                                                let e =
                                                    {
                                                        Range = d.Range
                                                        NewText = qual
                                                    }
                                                let edit =
                                                    {
                                                        TextDocument =
                                                            {
                                                                Uri = p.TextDocument.Uri
                                                                Version = commands.TryGetFileVersion fn
                                                            }
                                                        Edits = [|e|]
                                                    }
                                                let we = WorkspaceEdit.Create([|edit|], clientCapabilities.Value)


                                                { CodeAction.Title = sprintf "Use %s" qual
                                                  Kind = Some "quickfix"
                                                  Diagnostics = Some [| d |]
                                                  Edit = we
                                                  Command = None}
                                            )
                                        let ops =
                                            opens
                                            |> List.map (fun (ns, name, ctx, multiple) ->
                                                let insertPoint = adjustInsertionPoint lines ctx
                                                let docLine = insertPoint - 1
                                                let s =
                                                    if name.EndsWith word && name <> word then
                                                        let prefix = name.Substring(0, name.Length - word.Length).TrimEnd('.')
                                                        ns + "." + prefix
                                                    else ns



                                                let lineStr = (String.replicate ctx.Pos.Column " ") + "open " + s + "\n"
                                                let edits =
                                                    [|
                                                        yield insertLine docLine lineStr
                                                        if lines.[docLine + 1].Trim() <> "" then yield insertLine (docLine + 1) ""
                                                        if (ctx.Pos.Column = 0 || ctx.ScopeKind = Namespace) && docLine > 0 && not ((lines.[docLine - 1]).StartsWith "open" ) then
                                                            yield insertLine (docLine - 1) ""
                                                    |]
                                                let edit =
                                                    {
                                                        TextDocument =
                                                            {
                                                                Uri = p.TextDocument.Uri
                                                                Version = commands.TryGetFileVersion fn
                                                            }
                                                        Edits = edits
                                                    }
                                                let we = WorkspaceEdit.Create([|edit|], clientCapabilities.Value)


                                                { CodeAction.Title = sprintf "Open %s" s
                                                  Kind = Some "quickfix"
                                                  Diagnostics = Some [| d |]
                                                  Edit = we
                                                  Command = None}

                                            )
                                        [yield! ops; yield! quals; ]
                                    | _ -> []
                                return res
                            }
                        )
                })
        else
            async.Return []

    override x.TextDocumentCodeAction(p) =
        Debug.print "[LSP call] TextDocumentCodeAction"
        let fn = p.TextDocument.GetFilePath()
        match commands.TryGetFileCheckerOptionsWithLines fn with
        | ResultOrString.Error s ->
            AsyncLspResult.internalError s
        | ResultOrString.Ok (opts, lines) ->
        async {
            let! unusedOpensActions = x.GetUnusedOpensCodeActions fn p
            let! resolveNamespaceActions = x.GetResolveNamespaceActions fn p
            let! errorSuggestionActions = x.GetErrorSuggestionsCodeActions fn p
            let! unusedActions = x.GetUnusedCodeAction fn p lines
            let! redundantActions = x.GetRedundantQualfierCodeAction fn p
            let! newKeywordAction = x.GetNewKeywordSuggestionCodeAction fn p lines
            let! duCaseActions = x.GetUnionCaseGeneratorCodeAction fn p lines
            let! linterActions = x.GetLinterCodeAction fn p
            let! interfaceGenerator = x.GetInterfaceStubCodeAction fn p lines
            let! recordGenerator = x.GetRecordStubCodeAction fn p lines


            let res =
                [|
                    yield! unusedOpensActions
                    yield! resolveNamespaceActions
                    yield! errorSuggestionActions
                    yield! unusedActions
                    yield! newKeywordAction
                    yield! duCaseActions
                    yield! linterActions
                    yield! interfaceGenerator
                    yield! recordGenerator
                    yield! redundantActions
                |]


            return res |> TextDocumentCodeActionResult.CodeActions |> Some |> success
        }

    override __.TextDocumentCodeLens(p) = async {
        Debug.print "[LSP call] TextDocumentCodeLens"
        let fn = p.TextDocument.GetFilePath()
        let! res = commands.Declarations fn None (commands.TryGetFileVersion fn)
        let res =
            if config.LineLens.Enabled <> "replaceCodeLens" then
                match res.[0] with
                | CoreResponse.InfoRes msg | CoreResponse.ErrorRes msg ->
                    LspResult.internalError msg
                | CoreResponse.Declarations (decls) ->
                    let res =
                        decls
                        |> Array.map (fst >> getCodeLensInformation p.TextDocument.Uri "signature")
                        |> Array.collect id
                    let res2 =
                        if config.EnableReferenceCodeLens then
                            decls
                            |> Array.map (fst >> getCodeLensInformation p.TextDocument.Uri "reference")
                            |> Array.collect id
                        else
                            [||]

                    [| yield! res2; yield! res |]
                    |> Some
                    |> success
                | _ -> LspResult.notImplemented
            else
                [| |]
                |> Some
                |> success
        return res
    }

    override __.CodeLensResolve(p) =
        Debug.print "[LSP call] CodeLensResolve"
        let handler f (arg: CodeLens) =
            async {
                let pos = FcsRange.mkPos (arg.Range.Start.Line + 1) (arg.Range.Start.Character + 2)
                let data = arg.Data.Value.ToObject<string[]>()
                let file = fileUriToLocalPath data.[0]
                Debug.print "[LSP] CodeLensResolve - Position request: %s at %A" file pos
                return!
                    match commands.TryGetFileCheckerOptionsWithLinesAndLineStr(file, pos) with
                    | ResultOrString.Error s ->
                        Debug.print "[LSP] CodeLensResolve - Getting file checker options failed: %s" s
                        let cmd = {Title = "No options"; Command = None; Arguments = None}
                        {p with Command = Some cmd} |> success |> async.Return
                    | ResultOrString.Ok (options, _, lineStr) ->
                        try
                            async {
                                let! tyResOpt = commands.TryGetLatestTypeCheckResultsForFile(file)
                                return!
                                    match tyResOpt with
                                    | None ->
                                        Debug.print "[LSP] CodeLensResolve - Cached typecheck results not yet available"
                                        let cmd = {Title = "No typecheck results"; Command = None; Arguments = None}
                                        {p with Command = Some cmd} |> success |> async.Return
                                    | Some tyRes ->
                                        async {
                                            let! r = Async.Catch (f arg pos tyRes lineStr data.[1] file)
                                            match r with
                                            | Choice1Of2 r -> return r
                                            | Choice2Of2 e ->
                                                Debug.print "[LSP] CodeLensResolve - Operation failed: %s" e.Message
                                                let cmd = {Title = ""; Command = None; Arguments = None}
                                                return {p with Command = Some cmd} |> success
                                        }
                            }
                        with e ->
                            Debug.print "[LSP] CodeLensResolve - Operation failed: %s" e.Message
                            let cmd = {Title = ""; Command = None; Arguments = None}
                            {p with Command = Some cmd} |> success |> async.Return
            }


        handler (fun p pos tyRes lineStr typ file ->
            async {
                if typ = "signature" then
                    let! res = commands.SignatureData tyRes pos lineStr
                    let res =
                        match res.[0] with
                        | CoreResponse.InfoRes msg | CoreResponse.ErrorRes msg ->
                            Debug.print "[LSP] CodeLensResolve - error: %s" msg
                            let cmd = {Title = ""; Command = None; Arguments = None}
                            {p with Command = Some cmd} |> success
                        | CoreResponse.SignatureData (typ, parms, _) ->
                            let formatted = SigantureData.formatSignature typ parms
                            let cmd = {Title = formatted; Command = None; Arguments = None}
                            {p with Command = Some cmd} |> success
                        | _ ->
                            let cmd = {Title = ""; Command = None; Arguments = None}
                            {p with Command = Some cmd} |> success
                    return res
                else
                    let! res = commands.SymbolUseProject tyRes pos lineStr
                    let res =
                        match res.[0] with
                        | CoreResponse.InfoRes msg | CoreResponse.ErrorRes msg ->
                            Debug.print "[LSP] CodeLensResolve - error: %s" msg
                            let cmd = {Title = ""; Command = None; Arguments = None}
                            {p with Command = Some cmd} |> success
                        | CoreResponse.SymbolUse (sym, uses) ->
                            let formatted =
                                if uses.Length = 1 then "1 Reference"
                                else sprintf "%d References" uses.Length
                            let locs =
                                uses
                                |> Array.map (fun n -> fcsRangeToLspLocation n.RangeAlternate)

                            let args = [|
                                JToken.FromObject (filePathToUri file)
                                JToken.FromObject (fcsPosToLsp pos)
                                JToken.FromObject locs
                            |]

                            let cmd = {Title = formatted; Command = Some "fsharp.showReferences"; Arguments = Some args}
                            {p with Command = Some cmd} |> success
                        | CoreResponse.SymbolUseRange (uses) ->
                            let formatted =
                                if uses.Length - 1 = 1 then "1 Reference"
                                elif uses.Length = 0 then "0 References"
                                else sprintf "%d References" (uses.Length - 1)
                            let locs =
                                uses
                                |> Array.map symbolUseRangeToLspLocation

                            let args = [|
                                JToken.FromObject (filePathToUri file)
                                JToken.FromObject (fcsPosToLsp pos)
                                JToken.FromObject locs
                            |]

                            let cmd = {Title = formatted; Command = Some "fsharp.showReferences"; Arguments = Some args}
                            {p with Command = Some cmd} |> success
                        | _ ->
                            let cmd = {Title = ""; Command = None; Arguments = None}
                            {p with Command = Some cmd} |> success
                    return res
            }
        ) p

    override __.WorkspaceDidChangeWatchedFiles(p) = async {
        Debug.print "[LSP call] WorkspaceDidChangeWatchedFiles"
        p.Changes
        |> Array.iter (fun c ->
            if c.Type = FileChangeType.Deleted then
                let uri = c.Uri
                diagnosticCollections.AddOrUpdate((uri, "F# Compiler"), [||], fun _ _ -> [||]) |> ignore
                diagnosticCollections.AddOrUpdate((uri, "F# Unused opens"), [||], fun _ _ -> [||]) |> ignore
                diagnosticCollections.AddOrUpdate((uri, "F# Unused declarations"), [||], fun _ _ -> [||]) |> ignore
                diagnosticCollections.AddOrUpdate((uri, "F# simplify names"), [||], fun _ _ -> [||]) |> ignore
                diagnosticCollections.AddOrUpdate((uri, "F# Linter"), [||], fun _ _ -> [||]) |> ignore
            ()
        )

        return ()
    }

    override __.WorkspaceDidChangeConfiguration(p) = async {
        let dto =
            p.Settings
            |> Server.deserialize<FSharpConfigRequest>
        let c = config.AddDto dto.FSharp
        updateConfig c
        Debug.print "[LSP call] WorkspaceDidChangeConfiguration:\n %A" c
        return ()
    }

    override __.TextDocumentFoldingRange(rangeP: FoldingRangeParams) = async {
        Debug.print "[LSP call] TextDocument/FoldingRange"
        let file = rangeP.TextDocument.GetFilePath()
        match! commands.ScopesForFile file with
        | Ok scopes ->
            let ranges = scopes |> Seq.map toFoldingRange |> Set.ofSeq |> List.ofSeq
            return LspResult.success (Some ranges)
        | Result.Error error ->
            return LspResult.internalError error
    }

    member x.FSharpSignature(p: TextDocumentPositionParams) =
        Debug.print "[LSP call] FSharpSignature"
        p |> x.positionHandler (fun p pos tyRes lineStr lines ->
            async {
                let! res = commands.Typesig tyRes pos lineStr
                let res =
                    match res.[0] with
                    | CoreResponse.InfoRes msg | CoreResponse.ErrorRes msg ->
                        LspResult.internalError msg
                    | CoreResponse.TypeSig tip ->
                        { Content =  CommandResponse.typeSig FsAutoComplete.JsonSerializer.writeJson tip }
                        |> success
                    | _ -> LspResult.notImplemented

                return res
            }
        )

    member x.FSharpSignatureData(p: TextDocumentPositionParams) =
        Debug.print "[LSP call] FSharpSignatureData"
        let handler f (arg: TextDocumentPositionParams) =
            async {
                let pos = FcsRange.mkPos (p.Position.Line) (p.Position.Character + 2)
                let file = IO.Path.GetFullPath (p.TextDocument.Uri)
                Debug.print "[LSP] FSharpSignatureData - Position request: %s at %A" file pos
                return!
                    match commands.TryGetFileCheckerOptionsWithLinesAndLineStr(file, pos) with
                    | ResultOrString.Error s ->
                        AsyncLspResult.internalError "No options"
                    | ResultOrString.Ok (options, _, lineStr) ->
                        try
                            async {
                                let! tyResOpt = commands.TryGetLatestTypeCheckResultsForFile(file)
                                return!
                                    match tyResOpt with
                                    | None ->
                                        AsyncLspResult.internalError "No typecheck results"
                                    | Some tyRes ->
                                        async {
                                            let! r = Async.Catch (f arg pos tyRes lineStr)
                                            match r with
                                            | Choice1Of2 r -> return r
                                            | Choice2Of2 e ->
                                                return LspResult.internalError e.Message
                                        }
                            }
                        with e ->
                            AsyncLspResult.internalError e.Message
            }

        p |> handler (fun p pos tyRes lineStr ->
            async {
                let! res = commands.SignatureData tyRes pos lineStr
                let res =
                    match res.[0] with
                    | CoreResponse.InfoRes msg | CoreResponse.ErrorRes msg ->
                        LspResult.internalError msg
                    | CoreResponse.SignatureData (typ, parms, generics) ->
                        { Content =  CommandResponse.signatureData FsAutoComplete.JsonSerializer.writeJson (typ, parms, generics) }
                        |> success
                    | _ -> LspResult.notImplemented

                return res
            }
        )

    member x.FSharpDocumentationGenerator(p: TextDocumentPositionParams) =
        Debug.print "[LSP call] FSharpDocumentationGenerator"
        p |> x.positionHandler (fun p pos tyRes lineStr lines ->
            async {
                let! res = commands.SignatureData tyRes pos lineStr
                let res =
                    match res.[0] with
                    | CoreResponse.InfoRes msg | CoreResponse.ErrorRes msg ->
                        LspResult.internalError msg
                    | CoreResponse.SignatureData (typ, parms, generics) ->
                        { Content =  CommandResponse.signatureData FsAutoComplete.JsonSerializer.writeJson (typ, parms, generics) }
                        |> success
                    | _ -> LspResult.notImplemented

                return res
            }
        )

    member __.FSharpLineLense(p) = async {
        Debug.print "[LSP call] FSharpLineLense"
        let fn = p.Project.GetFilePath()
        let! res = commands.Declarations fn None (commands.TryGetFileVersion fn)
        let res =
            match res.[0] with
            | CoreResponse.InfoRes msg | CoreResponse.ErrorRes msg ->
                LspResult.internalError msg
            | CoreResponse.Declarations (decls) ->
                { Content =  CommandResponse.declarations FsAutoComplete.JsonSerializer.writeJson decls }
                |> success
            | _ -> LspResult.notImplemented
        return res
    }

    member x.LineLensResolve(p) =
        Debug.print "[LSP call] LineLensResolve"
        p |> x.positionHandler (fun p pos tyRes lineStr lines ->
            async {
                let! res = commands.SignatureData tyRes pos lineStr
                let res =
                    match res.[0] with
                    | CoreResponse.InfoRes msg | CoreResponse.ErrorRes msg ->
                        LspResult.internalError msg
                    | CoreResponse.SignatureData(typ, parms, generics) ->
                        { Content =  CommandResponse.signatureData FsAutoComplete.JsonSerializer.writeJson (typ, parms, generics) }
                        |> success
                    | _ -> LspResult.notImplemented

                return res
            }
        )

    member __.FSharpCompilerLocation(p) = async {
        Debug.print "[LSP call] FSharpCompilerLocation"
        let res = commands.CompilerLocation ()
        let res =
            match res.[0] with
            | CoreResponse.InfoRes msg | CoreResponse.ErrorRes msg ->
                LspResult.internalError msg
            | CoreResponse.CompilerLocation(fsc, fsi, msbuild, sdk) ->
                { Content =  CommandResponse.compilerLocation FsAutoComplete.JsonSerializer.writeJson fsc fsi msbuild sdk}
                |> success
            | _ -> LspResult.notImplemented

        return res
    }

    member __.FSharpCompile(p) = async {
        Debug.print "[LSP call] FSharpCompile"
        let fn = p.Project.GetFilePath()
        let! res = commands.Compile fn
        let res =
            match res.[0] with
            | CoreResponse.InfoRes msg | CoreResponse.ErrorRes msg ->
                LspResult.internalError msg
            | CoreResponse.Compile(ers, code) ->
                { Content =  CommandResponse.compile FsAutoComplete.JsonSerializer.writeJson (ers, code) }
                |> success
            | _ -> LspResult.notImplemented

        return res
    }

    member __.FSharpWorkspaceLoad(p) = async {
        Debug.print "[LSP call] FSharpWorkspaceLoad"
        let fns = p.TextDocuments |> Array.map (fun fn -> fn.GetFilePath() ) |> Array.toList
        let! res = commands.WorkspaceLoad ignore fns config.DisableInMemoryProjectReferences config.ScriptTFM
        let res =
            match res.[0] with
            | CoreResponse.InfoRes msg | CoreResponse.ErrorRes msg ->
                LspResult.internalError msg
            | CoreResponse.WorkspaceLoad fin ->
                { Content =  CommandResponse.workspaceLoad FsAutoComplete.JsonSerializer.writeJson fin }
                |> success
            | _ -> LspResult.notImplemented

        return res
    }

    member __.FSharpWorkspacePeek(p: WorkspacePeekRequest) = async {
        Debug.print "[LSP call] FSharpWorkspacePeek"
        let! res = commands.WorkspacePeek p.Directory p.Deep (p.ExcludedDirs |> List.ofArray)
        let res =
            match res.[0] with
            | CoreResponse.InfoRes msg | CoreResponse.ErrorRes msg ->
                LspResult.internalError msg
            | CoreResponse.WorkspacePeek found ->
                { Content =  CommandResponse.workspacePeek FsAutoComplete.JsonSerializer.writeJson found }
                |> success
            | _ -> LspResult.notImplemented

        return res


    }

    member __.FSharpProject(p) = async {
        Debug.print "[LSP call] FSharpProject"
        let fn = p.Project.GetFilePath()
        let! res = commands.Project fn false ignore config.ScriptTFM
        let res =
            match res.[0] with
            | CoreResponse.InfoRes msg | CoreResponse.ErrorRes msg ->
                LspResult.internalError msg
            | CoreResponse.Project (fn, files, outFile, refs, logMap, extra, projItems, adds) ->
                { Content =  CommandResponse.project FsAutoComplete.JsonSerializer.writeJson (fn, files, outFile, refs, logMap, extra, projItems, adds) }
                |> success
            | CoreResponse.ProjectError er ->
                { Content =  CommandResponse.projectError FsAutoComplete.JsonSerializer.writeJson er }
                |> success
            | _ -> LspResult.notImplemented

        return res
    }

    member __.FSharpFsdn(p: FsdnRequest) = async {
        Debug.print "[LSP call] FSharpFsdn"
        let! res = commands.Fsdn p.Query
        let res =
            match res.[0] with
            | CoreResponse.InfoRes msg | CoreResponse.ErrorRes msg ->
                LspResult.internalError msg
            | CoreResponse.Fsdn (funcs) ->
                { Content = CommandResponse.fsdn FsAutoComplete.JsonSerializer.writeJson funcs }
                |> success
            | _ -> LspResult.notImplemented

        return res
    }

    member __.FSharpDotnetNewList(p: DotnetNewListRequest) = async {
        Debug.print "[LSP call] FSharpDotnetNewList"
        let! res = commands.DotnetNewList p.Query
        let res =
            match res.[0] with
            | CoreResponse.InfoRes msg | CoreResponse.ErrorRes msg ->
                LspResult.internalError msg
            | CoreResponse.DotnetNewList (funcs) ->
                { Content = CommandResponse.dotnetnewlist FsAutoComplete.JsonSerializer.writeJson funcs }
                |> success
            | _ -> LspResult.notImplemented

        return res
    }

    member __.FSharpDotnetNewGetDetails(p: DotnetNewGetDetailsRequest) = async {
        Debug.print "[LSP call] FSharpDotnetNewGetDetails"
        let! res = commands.DotnetNewGetDetails p.Query
        let res =
            match res.[0] with
            | CoreResponse.InfoRes msg | CoreResponse.ErrorRes msg ->
                LspResult.internalError msg
            | CoreResponse.DotnetNewGetDetails (funcs) ->
                { Content = CommandResponse.dotnetnewgetDetails FsAutoComplete.JsonSerializer.writeJson funcs }
                |> success
            | _ -> LspResult.notImplemented

        return res
    }

    member x.FSharpHelp(p: TextDocumentPositionParams) =
        Debug.print "[LSP call] FSharpHelp"
        p |> x.positionHandler (fun p pos tyRes lineStr lines ->
            async {
                let! res = commands.Help tyRes pos lineStr
                let res =
                    match res.[0] with
                    | CoreResponse.InfoRes msg | CoreResponse.ErrorRes msg ->
                        LspResult.internalError msg
                    | CoreResponse.Help(t) ->
                        { Content =  CommandResponse.help FsAutoComplete.JsonSerializer.writeJson t }
                        |> success
                    | _ -> LspResult.notImplemented

                return res
            }
        )

    member x.FSharpDocumentation(p: TextDocumentPositionParams) =
        Debug.print "[LSP call] FSharpDocumentation"
        p |> x.positionHandler (fun p pos tyRes lineStr lines ->
            async {
                let! res = commands.FormattedDocumentation tyRes pos lineStr
                let res =
                    match res.[0] with
                    | CoreResponse.InfoRes msg | CoreResponse.ErrorRes msg ->
                        LspResult.internalError msg
                    | CoreResponse.FormattedDocumentation(tip, xml, signature, footer, cm) ->
                        { Content =  CommandResponse.formattedDocumentation FsAutoComplete.JsonSerializer.writeJson (tip, xml, signature, footer, cm) }
                        |> success
                    | _ -> LspResult.notImplemented

                return res
            }
        )


    member x.FSharpDocumentationSymbol(p: DocumentationForSymbolReuqest) =
        Debug.print "[LSP call] FSharpDocumentationSymbol"
        match commands.LastCheckResult with
        | None -> AsyncLspResult.internalError "error"
        | Some tyRes ->
            async {
                let! res = commands.FormattedDocumentationForSymbol tyRes p.XmlSig p.Assembly
                let res =
                    match res.[0] with
                    | CoreResponse.InfoRes msg | CoreResponse.ErrorRes msg ->
                        LspResult.internalError msg
                    | CoreResponse.FormattedDocumentationForSymbol (xml, assembly, doc, signature, footer, cn) ->
                        { Content = CommandResponse.formattedDocumentationForSymbol FsAutoComplete.JsonSerializer.writeJson xml assembly doc (signature, footer, cn) }
                        |> success
                    | _ -> LspResult.notImplemented

                return res
            }

    member __.FakeTargets(p:FakeTargetsRequest) = async {
        Debug.print "[LSP call] FakeTargets"
        let! res = commands.FakeTargets (p.FileName) (p.FakeContext)
        let res =
            match res.[0] with
            | CoreResponse.InfoRes msg | CoreResponse.ErrorRes msg ->
                LspResult.internalError msg
            | CoreResponse.FakeTargets (targets) ->
                { Content = CommandResponse.fakeTargets FsAutoComplete.JsonSerializer.writeJson targets }
                |> success
            | _ -> LspResult.notImplemented
        return res
    }

    member __.FakeRuntimePath(p) = async {
        Debug.print "[LSP call] FakeRuntime"
        let! res = commands.FakeRuntime ()
        let res =
            match res.[0] with
            | CoreResponse.InfoRes msg | CoreResponse.ErrorRes msg ->
                LspResult.internalError msg
            | CoreResponse.FakeRuntime (runtimePath) ->
                { Content = CommandResponse.fakeRuntime FsAutoComplete.JsonSerializer.writeJson runtimePath }
                |> success
            | _ -> LspResult.notImplemented
        return res
    }

let startCore (commands: Commands) =
    use input = Console.OpenStandardInput()
    use output = Console.OpenStandardOutput()

    let requestsHandlings =
        defaultRequestHandlings<FsharpLspServer> ()
        |> Map.add "fsharp/signature" (requestHandling (fun s p -> s.FSharpSignature(p) ))
        |> Map.add "fsharp/signatureData" (requestHandling (fun s p -> s.FSharpSignatureData(p) ))
        |> Map.add "fsharp/documentationGenerator" (requestHandling (fun s p -> s.FSharpDocumentationGenerator(p) ))
        |> Map.add "fsharp/lineLens" (requestHandling (fun s p -> s.FSharpLineLense(p) ))
        |> Map.add "fsharp/compilerLocation" (requestHandling (fun s p -> s.FSharpCompilerLocation(p) ))
        |> Map.add "fsharp/compile" (requestHandling (fun s p -> s.FSharpCompile(p) ))
        |> Map.add "fsharp/workspaceLoad" (requestHandling (fun s p -> s.FSharpWorkspaceLoad(p) ))
        |> Map.add "fsharp/workspacePeek" (requestHandling (fun s p -> s.FSharpWorkspacePeek(p) ))
        |> Map.add "fsharp/project" (requestHandling (fun s p -> s.FSharpProject(p) ))
        |> Map.add "fsharp/fsdn" (requestHandling (fun s p -> s.FSharpFsdn(p) ))
        |> Map.add "fsharp/dotnetnewlist" (requestHandling (fun s p -> s.FSharpDotnetNewList(p) ))
        |> Map.add "fsharp/dotnetnewgetDetails" (requestHandling (fun s p -> s.FSharpDotnetNewGetDetails(p) ))
        |> Map.add "fsharp/f1Help" (requestHandling (fun s p -> s.FSharpHelp(p) ))
        |> Map.add "fsharp/documentation" (requestHandling (fun s p -> s.FSharpDocumentation(p) ))
        |> Map.add "fsharp/documentationSymbol" (requestHandling (fun s p -> s.FSharpDocumentationSymbol(p) ))
        |> Map.add "fake/listTargets" (requestHandling (fun s p -> s.FakeTargets(p) ))
        |> Map.add "fake/runtimePath" (requestHandling (fun s p -> s.FakeRuntimePath(p) ))



    LanguageServerProtocol.Server.start requestsHandlings input output FSharpLspClient (fun lspClient -> FsharpLspServer(commands, lspClient))

let start (commands: Commands) (_args: ParseResults<Options.CLIArguments>) =
    // stdout is used for commands
    if Debug.output = stdout then
        Debug.output <- stderr
    try
        let result = startCore commands
        Debug.print "[LSP] Start - Ending LSP mode with %A" result
        int result
    with
    | ex ->
        Debug.print "[LSP] Start - LSP mode crashed with %A" ex
        3
