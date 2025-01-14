namespace FsAutoComplete

open System
open System.IO
open FSharp.Compiler.SourceCodeServices
open FsAutoComplete.UnionPatternMatchCaseGenerator
open FsAutoComplete.RecordStubGenerator
open FsAutoComplete.InterfaceStubGenerator
open FsAutoComplete.DotnetNewTemplate
open System.Threading
open Utils
open System.Reflection
open FSharp.Compiler.Range
open FSharp.Analyzers

[<RequireQualifiedAccess>]
type CoreResponse =
    | InfoRes of text: string
    | ErrorRes of text: string
    | HelpText of name: string * tip: FSharpToolTipText * additionalEdit: (string * int * int * string) option
    | HelpTextSimple of name: string * tip: string
    | Project of projectFileName: ProjectFilePath * projectFiles: List<SourceFilePath> * outFileOpt : string option * references : ProjectFilePath list * logMap : Map<string,string> * extra: Dotnet.ProjInfo.Workspace.ExtraProjectInfoData * projectItems: Dotnet.ProjInfo.Workspace.ProjectViewerItem list * additionals : Map<string,string>
    | ProjectError of errorDetails: GetProjectOptionsErrors
    | ProjectLoading of projectFileName: ProjectFilePath
    | WorkspacePeek of found: WorkspacePeek.Interesting list
    | WorkspaceLoad of finished: bool
    | Completion of decls: FSharpDeclarationListItem[] * includeKeywords: bool
    | SymbolUse of symbol: FSharpSymbolUse * uses: FSharpSymbolUse[]
    | SymbolUseImplementation of symbol: FSharpSymbolUse * uses: FSharpSymbolUse[]
    | SignatureData of typ: string * parms: (string * string) list list * generics : string list
    | Help of data: string
    | Methods of meth: FSharpMethodGroup * commas: int
    | Errors of errors: FSharpErrorInfo[] * file: string
    | Colorizations of colorizations: (range * SemanticClassificationType) []
    | FindDeclaration of result: FindDeclarationResult
    | FindTypeDeclaration of range: range
    | Declarations of decls: (FSharpNavigationTopLevelDeclaration * string) []
    | ToolTip of tip: FSharpToolTipText<string> * signature: string * footer: string * typeDoc: string option
    | FormattedDocumentation of tip: FSharpToolTipText<string> option * xmlSig: (string * string) option * signature: (string * (string [] * string [] * string [] * string [] * string [] * string [])) * footer: string * cn: string
    | FormattedDocumentationForSymbol of xmlSig: string * assembly: string * xmlDoc: string list * signature: (string * (string [] * string [] * string [] * string [] * string [] * string [])) * footer: string * cn: string
    | TypeSig of tip: FSharpToolTipText<string>
    | CompilerLocation of fcs: string option * fsi: string option * msbuild: string option * sdkRoot: string option
    | Lint of file: string * warningsWithCodes: Lint.EnrichedLintWarning list
    | ResolveNamespaces of word: string * opens: (string * string * InsertContext * bool) list * qualifies: (string * string) list
    | UnionCase of text: string * position: pos
    | RecordStub of text: string * position: pos
    | InterfaceStub of text: string * position: pos
    | UnusedDeclarations of file: string * decls: (range * bool)[]
    | UnusedOpens of file: string * opens: range[]
    | SimplifiedName of file: string * names: (range * string)[]
    | Compile of errors: FSharp.Compiler.SourceCodeServices.FSharpErrorInfo[] * code: int
    | Analyzer of messages: SDK.Message [] * file: string
    | SymbolUseRange of ranges: SymbolCache.SymbolUseRange[]
    | SymbolUseImplementationRange of ranges: SymbolCache.SymbolUseRange[]
    | RangesAtPositions of ranges: range list list
    | Fsdn of string list
    | FakeTargets of result: FakeSupport.GetTargetsResult
    | FakeRuntime of runtimePath: string
    | DotnetNewList of Template list
    | DotnetNewGetDetails of DetailedTemplate
    | DotnetNewCreateCli of commandName: string * parameterStr: string

[<RequireQualifiedAccess>]
type NotificationEvent =
    | ParseError of CoreResponse
    | Workspace of CoreResponse
    | AnalyzerMessage of CoreResponse
    | UnusedOpens of CoreResponse
    | Lint of CoreResponse
    | Analyzer of CoreResponse
    | UnusedDeclarations of CoreResponse
    | SimplifyNames of CoreResponse
    | Canceled of CoreResponse
    | Diagnostics of LanguageServerProtocol.Types.PublishDiagnosticsParams
    | FileParsed of string

type Commands (serialize : Serializer, backgroundServiceEnabled) =
    let checker = FSharpCompilerServiceChecker(backgroundServiceEnabled)
    let state = State.Initial
    let fileParsed = Event<FSharpParseFileResults>()
    let fileChecked = Event<ParseAndCheckResults * string * int>()
    let mutable lastVersionChecked = -1
    let mutable lastCheckResult : ParseAndCheckResults option = None
    let mutable isWorkspaceReady = false

    let notify = Event<NotificationEvent>()

    let workspaceReady = Event<unit>()

    let fileStateSet = Event<unit>()

    do BackgroundServices.messageRecived.Publish.Add (fun n ->
       match n with
       | BackgroundServices.Diagnostics d -> notify.Trigger (NotificationEvent.Diagnostics d)
    )

    do fileParsed.Publish.Add (fun parseRes ->
        let decls = parseRes.GetNavigationItems().Declarations
        state.NavigationDeclarations.[parseRes.FileName] <- decls
        state.ParseResults.[parseRes.FileName] <- parseRes
    )

    do if not backgroundServiceEnabled then
            checker.FileChecked.Add (fun (n,_) ->
                Debug.print "[Commands - checker events] File checked - %s" n
                async {
                    try
                        match state.FileCheckOptions.TryGetValue(n) with
                        | true, opts ->
                            let! res = checker.GetBackgroundCheckResultsForFileInProject(n, opts)
                            fileChecked.Trigger (res, res.FileName, -1)
                        | _ -> ()
                    with
                    | _ -> ()
                } |> Async.Start
            )

    //Triggered by `FSharpChecker.FileChecked` if background service is disabled; and by `Parse` command
    do fileChecked.Publish.Add (fun (parseAndCheck, file, version) ->
        async {
            try
                NotificationEvent.FileParsed file
                |> notify.Trigger

                let checkErrors = parseAndCheck.GetParseResults.Errors
                let parseErrors = parseAndCheck.GetCheckResults.Errors
                let errors =
                    Array.append checkErrors parseErrors
                    |> Array.distinctBy (fun e -> e.Severity, e.ErrorNumber, e.StartLineAlternate, e.StartColumn, e.EndLineAlternate, e.EndColumn, e.Message)
                CoreResponse.Errors (errors, file)
                |> NotificationEvent.ParseError
                |> notify.Trigger
            with
            | _ -> ()
        }
        |> Async.Start

        async {
            try
                let analyzers = state.Analyzers.Values |> Seq.collect id
                if analyzers |> Seq.length > 0 then
                    match parseAndCheck.GetParseResults.ParseTree, parseAndCheck.GetCheckResults.ImplementationFile with
                    | Some pt, Some tast ->
                        let context : SDK.Context = {
                            FileName = file
                            Content = state.Files.[file].Lines
                            ParseTree = pt
                            TypedTree = tast
                            Symbols = parseAndCheck.GetCheckResults.PartialAssemblySignature.Entities |> Seq.toList
                        }
                        let result = analyzers |> Seq.collect (fun n -> n context)
                        CoreResponse.Analyzer (Seq.toArray result, file)
                        |> NotificationEvent.AnalyzerMessage
                        |> notify.Trigger
                    | _ -> ()

            with
            | _ -> ()
        } |> Async.Start
    )


    let normalizeOptions (opts : FSharpProjectOptions) =
        { opts with
            SourceFiles = opts.SourceFiles |> Array.map (Path.GetFullPath)
            OtherOptions = opts.OtherOptions |> Array.map (fun n -> if FscArguments.isCompileFile(n) then Path.GetFullPath n else n)
        }

    let parseFilesInTheBackground fsiScriptTFM files =
        async {
            files
            |> List.toArray
            |> Array.Parallel.iter (fun file ->
                try
                    let sourceOpt =
                        match state.Files.TryFind file with
                        | Some f -> Some (f.Lines)
                        | None when File.Exists(file) ->
                            let ctn = File.ReadAllLines file
                            state.Files.[file] <- { Touched = DateTime.Now; Lines = ctn; Version = None }
                            let payload =
                                if Utils.isAScript file
                                then BackgroundServices.ScriptFile(file, fsiScriptTFM)
                                else BackgroundServices.SourceFile file
                            if backgroundServiceEnabled then BackgroundServices.updateFile(payload, ctn |> String.concat "\n", 0)
                            Some (ctn)
                        | None -> None
                    match sourceOpt with
                    | None -> ()
                    | Some source ->
                        let opts = state.FileCheckOptions.[file] |> Utils.projectOptionsToParseOptions
                        let parseRes = checker.ParseFile(file, source |> String.concat "\n", opts) |> Async.RunSynchronously
                        fileParsed.Trigger parseRes
                with
                | :? System.Threading.ThreadAbortException as ex ->
                    // on mono, if background parsing is aborted a ThreadAbortException
                    // is raised, who can be ignored
                    ()
                | ex ->
                    Debug.print "[Commands] Failed to parse file '%s' exn %A" file ex
            ) }

    let calculateNamespaceInser (decl : FSharpDeclarationListItem) (pos : pos) getLine =
        let getLine i =
            try
                getLine i
            with
            | _ -> ""
        let idents = decl.FullName.Split '.'
        decl.NamespaceToOpen
        |> Option.bind (fun n ->
            state.CurrentAST
            |> Option.map (fun ast -> ParsedInput.findNearestPointToInsertOpenDeclaration (pos.Line) ast idents TopLevel )
            |> Option.map (fun ic -> n, ic.Pos.Line, ic.Pos.Column, ic.ScopeKind.ToString()))

    let fillHelpTextInTheBackground decls (pos : pos) fn getLine =
        let declName (d: FSharpDeclarationListItem) = d.Name

        //Fill list of declarations synchronously to know which declarations should be in cache.
        for d in decls do
            state.Declarations.[declName d] <- (d, pos, fn)

        //Fill namespace insertion cache asynchronously.
        async {
            for decl in decls do
                let n = declName decl
                let insert = calculateNamespaceInser decl pos getLine
                if insert.IsSome then state.CompletionNamespaceInsert.[n] <- insert.Value
        } |> Async.Start

    let onProjectLoaded projectFileName (response: ProjectCrackerCache) tfmForScripts =
        for file in response.Items |> List.choose (function Dotnet.ProjInfo.Workspace.ProjectViewerItem.Compile(p, _) -> Some p) do
            state.FileCheckOptions.[file] <- normalizeOptions response.Options

        response.Items
        |> List.choose (function Dotnet.ProjInfo.Workspace.ProjectViewerItem.Compile(p, _) -> Some p)
        |> parseFilesInTheBackground tfmForScripts
        |> Async.Start

    let workspaceBinder () =
        let config = Dotnet.ProjInfo.Workspace.LoaderConfig.Default Environment.msbuildLocator
        let loader = Dotnet.ProjInfo.Workspace.Loader.Create(config)
        loader, checker.CreateFCSBinder(NETFrameworkInfoProvider.netFWInfo, loader)

    member __.Notify = notify.Publish

    member __.WorkspaceReady = workspaceReady.Publish

    member __.FileChecked = fileChecked.Publish


    member __.IsWorkspaceReady
        with get() = isWorkspaceReady
        and set(value) = isWorkspaceReady <- value

    member __.LastVersionChecked
        with get() = lastVersionChecked

    member __.LastCheckResult
        with get() = lastCheckResult

    member __.SetFileContent(file: SourceFilePath, lines: LineStr[], version, tfmIfScript) =
        state.AddFileText(file, lines, version)
        let payload =
            if Utils.isAScript file
            then BackgroundServices.ScriptFile(file, tfmIfScript)
            else BackgroundServices.SourceFile file

        if backgroundServiceEnabled then BackgroundServices.updateFile(payload, lines |> String.concat "\n", defaultArg version 0)

    member private x.MapResultAsync (successToString: 'a -> Async<CoreResponse>, ?failureToString: string -> CoreResponse) =
        Async.bind <| function
            // A failure is only info here, as this command is expected to be
            // used 'on idle', and frequent errors are expected.
            | ResultOrString.Error e -> async.Return [(defaultArg failureToString CoreResponse.InfoRes) e]
            | ResultOrString.Ok r -> successToString r |> Async.map List.singleton

    member private x.MapResult (successToString: 'a -> CoreResponse, ?failureToString: string -> CoreResponse) =
        x.MapResultAsync ((fun x -> successToString x |> async.Return), ?failureToString = failureToString)

    member x.Fsdn (querystr) = async {
            let results = Fsdn.query querystr
            return [ CoreResponse.Fsdn results ]
        }

    member x.DotnetNewList (filterstr) = async {
            let results = DotnetNewTemplate.dotnetnewlist filterstr
            return [ CoreResponse.DotnetNewList results ]
        }

    member x.DotnetNewGetDetails (filterstr) = async {
            let results = DotnetNewTemplate.dotnetnewgetDetails filterstr
            return [ CoreResponse.DotnetNewGetDetails results ]
        }

    member x.DotnetNewCreateCli (templateShortName : string) (parameterStr : (string * obj) list) = async {
            let results = DotnetNewTemplate.dotnetnewCreateCli templateShortName parameterStr
            return [ CoreResponse.DotnetNewCreateCli results ]
        }

    member private x.AsCancellable (filename : SourceFilePath) (action : Async<CoreResponse list>) =
        let cts = new CancellationTokenSource()
        state.AddCancellationToken(filename, cts)
        Async.StartCatchCancellation(action, cts.Token)
        |> Async.Catch
        |> Async.map (function
            | Choice1Of2 res -> res
            | Choice2Of2 err ->
                let cld = CoreResponse.InfoRes (sprintf "Request cancelled (exn was %A)" err)
                notify.Trigger (NotificationEvent.Canceled cld)
                [cld])

    member private x.CancelQueue (filename : SourceFilePath) =
        let filename = Path.GetFullPath filename
        state.GetCancellationTokens filename |> List.iter (fun cts -> cts.Cancel() )

    member x.TryGetRecentTypeCheckResultsForFile(file, opts) =
        let file = Path.GetFullPath file
        checker.TryGetRecentCheckResultsForFile(file, opts)

    ///Gets recent type check results, waiting for the results of in-progress type checking
    /// if version of file in memory is grater than last type checked version.
    /// It also waits if there are no FSharpProjectOptions avaliable for given file
    member x.TryGetLatestTypeCheckResultsForFile(file) =
        let file = Path.GetFullPath file
        let stateVersion = state.TryGetFileVersion file
        let checkedVersion = state.TryGetLastCheckedVersion file
        Debug.print "[Commands] TryGetLatestTypeCheckResultsForFile - %s; State - %A; Checked - %A" file stateVersion checkedVersion
        match stateVersion, checkedVersion with
        | Some sv, Some cv when cv < sv ->
            x.FileChecked
            |> Event.filter (fun (_,n,_) -> n = file )
            |> Event.map ignore
            |> Async.AwaitEvent
            |> Async.bind (fun _ -> x.TryGetLatestTypeCheckResultsForFile(file))
        | Some _, None
        | None, Some _ ->
            x.FileChecked
            |> Event.filter (fun (_,n,_) -> n = file )
            |> Event.map ignore
            |> Async.AwaitEvent
            |> Async.bind (fun _ -> x.TryGetLatestTypeCheckResultsForFile(file))
        | _ ->
            match state.TryGetFileCheckerOptionsWithLines(file) with
            | ResultOrString.Ok (opts, _ ) ->
                x.TryGetRecentTypeCheckResultsForFile(file, opts)
                |> async.Return
            | ResultOrString.Error _ ->
                x.FileChecked
                |> Event.filter (fun (_,n,_) -> n = file )
                |> Event.map ignore
                |> Async.AwaitEvent
                |> Async.bind (fun _ -> x.TryGetLatestTypeCheckResultsForFile(file))




    member x.TryGetFileCheckerOptionsWithLinesAndLineStr(file, pos) =
        let file = Path.GetFullPath file
        state.TryGetFileCheckerOptionsWithLinesAndLineStr(file, pos)

    member x.TryGetFileCheckerOptionsWithLines(file) =
        let file = Path.GetFullPath file
        state.TryGetFileCheckerOptionsWithLines file

    member x.Files = state.Files

    member x.TryGetFileVersion = state.TryGetFileVersion

    member x.Parse file lines version (isSdkScript: bool option) =
        let file = Path.GetFullPath file
        let tmf = isSdkScript |> Option.map (fun n -> if n then FSIRefs.NetCore else FSIRefs.NetFx) |> Option.defaultValue FSIRefs.NetFx

        do x.CancelQueue file
        async {
            let colorizations = state.ColorizationOutput
            let parse' fileName text options =
                async {
                    let! result = checker.ParseAndCheckFileInProject(fileName, version, text, options)
                    return
                        match result with
                        | ResultOrString.Error e ->
                            [CoreResponse.ErrorRes e]
                        | ResultOrString.Ok (parseAndCheck) ->
                            let parseResult = parseAndCheck.GetParseResults
                            let results = parseAndCheck.GetCheckResults
                            do fileParsed.Trigger parseResult
                            do lastVersionChecked <- version
                            do lastCheckResult <- Some parseAndCheck
                            do state.SetLastCheckedVersion fileName version
                            do fileChecked.Trigger (parseAndCheck, fileName, version)
                            let errors = Array.append results.Errors parseResult.Errors
                            if colorizations then
                                [   CoreResponse.Errors (errors, fileName)
                                    CoreResponse.Colorizations (results.GetSemanticClassification None) ]
                            else [ CoreResponse.Errors (errors, fileName) ]
                }
            let text = String.concat "\n" lines

            if Utils.isAScript file then
                let! checkOptions = checker.GetProjectOptionsFromScript(file, text, tmf)
                state.AddFileTextAndCheckerOptions(file, lines, normalizeOptions checkOptions, Some version)
                fileStateSet.Trigger ()
                return! parse' file text checkOptions
            else
                let! checkOptions =
                    match state.GetCheckerOptions(file, lines) with
                    | Some c ->
                        state.SetFileVersion file version
                        async.Return c
                    | None -> async {
                        let! checkOptions = checker.GetProjectOptionsFromScript(file, text, tmf)
                        state.AddFileTextAndCheckerOptions(file, lines, normalizeOptions checkOptions, Some version)
                        return checkOptions
                    }
                fileStateSet.Trigger ()
                return! parse' file text checkOptions
        } |> x.AsCancellable file

    member private __.ToProjectCache (opts, extraInfo: Dotnet.ProjInfo.Workspace.ExtraProjectInfoData, projViewerItems: Dotnet.ProjInfo.Workspace.ProjectViewerItem list, logMap) =
        let outFileOpt = Some (extraInfo.TargetPath)
        let references = FscArguments.references (opts.OtherOptions |> List.ofArray)
        let fullPathNormalized = Path.GetFullPath >> Utils.normalizePath
        let projViewerItemsNormalized = if obj.ReferenceEquals(null, projViewerItems) then [] else projViewerItems
        let projViewerItemsNormalized =
            projViewerItemsNormalized
            |> List.map (function
                | Dotnet.ProjInfo.Workspace.ProjectViewerItem.Compile(p, c) ->
                    Dotnet.ProjInfo.Workspace.ProjectViewerItem.Compile(fullPathNormalized p, c))

        let cached = {
            ProjectCrackerCache.Options = opts
            OutFile = outFileOpt
            References = references
            Log = logMap
            ExtraInfo = extraInfo
            Items = projViewerItemsNormalized
        }

        (opts.ProjectFileName, cached)

    member x.Project projectFileName _verbose onChange tfmForScripts = async {
        let projectFileName = Path.GetFullPath projectFileName
        let project =
            match state.Projects.TryFind projectFileName with
            | Some prj -> prj
            | None ->
                let proj = new Project(projectFileName, onChange)
                state.Projects.[projectFileName] <- proj
                proj

        let workspaceBinder = workspaceBinder ()

        let projResponse =
            match project.Response with
            | Some response ->
                Result.Ok (projectFileName, response)
            | None ->
                let projectCached =
                    projectFileName
                    |> Workspace.parseProject workspaceBinder
                    |> Result.map (fun (opts, optsDPW, projectFiles, logMap) -> x.ToProjectCache(opts, optsDPW.ExtraProjectInfo, projectFiles, logMap) )
                match projectCached with
                | Result.Ok (projectFileName, response) ->
                    if backgroundServiceEnabled then BackgroundServices.updateProject(projectFileName, response.Options)
                    project.Response <- Some response
                    Result.Ok (projectFileName, response)
                | Result.Error error ->
                    project.Response <- None
                    Result.Error error
        return
            match projResponse with
            | Result.Ok (projectFileName, response) ->
                onProjectLoaded projectFileName response tfmForScripts
                let responseFiles =
                    response.Items
                    |> List.choose (function Dotnet.ProjInfo.Workspace.ProjectViewerItem.Compile(p, _) -> Some p)
                [ CoreResponse.Project (projectFileName, responseFiles, response.OutFile, response.References, response.Log, response.ExtraInfo, response.Items, Map.empty) ]
            | Result.Error error ->
                [ CoreResponse.ProjectError error ]
    }

    member x.Declarations file lines version = async {
        let file = Path.GetFullPath file
        match state.TryGetFileCheckerOptionsWithSource file, lines with
        | ResultOrString.Error s, None ->
            match state.TryGetFileSource file with
            | ResultOrString.Error s -> return [CoreResponse.ErrorRes s]
            | ResultOrString.Ok l ->
                let text = String.concat "\n" l
                let files = Array.singleton file
                let parseOptions = { FSharpParsingOptions.Default with SourceFiles = files}
                let! decls = checker.GetDeclarations(file, text, parseOptions, version)
                let decls = decls |> Array.map (fun a -> a,file)
                return [CoreResponse.Declarations decls]
        | ResultOrString.Error _, Some l ->
            let text = String.concat "\n" l
            let files = Array.singleton file
            let parseOptions = { FSharpParsingOptions.Default with SourceFiles = files}
            let! decls = checker.GetDeclarations(file, text, parseOptions, version)
            let decls = decls |> Array.map (fun a -> a,file)
            return [CoreResponse.Declarations decls]
        | ResultOrString.Ok (checkOptions, source), _ ->
            let text =
                match lines with
                | Some l -> String.concat "\n" l
                | None -> source

            let parseOptions = Utils.projectOptionsToParseOptions checkOptions
            let! decls = checker.GetDeclarations(file, text, parseOptions, version)

            state.NavigationDeclarations.[file] <- decls

            let decls = decls |> Array.map (fun a -> a,file)
            return [CoreResponse.Declarations decls]
    }

    member x.DeclarationsInProjects () = async {
        let decls =
            state.NavigationDeclarations.ToArray()
            |> Array.collect (fun (KeyValue(p, decls)) -> decls |> Array.map (fun d -> d,p))
        return [CoreResponse.Declarations decls]
    }

    member __.Helptext sym =
        match KeywordList.tryGetKeywordDescription sym with
        | Some s ->
            [CoreResponse.HelpTextSimple (sym, s)]
        | None ->
        match KeywordList.tryGetHashDescription sym with
        | Some s ->
            [CoreResponse.HelpTextSimple (sym, s)]
        | None ->
        match state.Declarations.TryFind sym with
        | None -> //Isn't in sync filled cache, we don't have result
            [CoreResponse.ErrorRes (sprintf "No help text available for symbol '%s'" sym)]
        | Some (decl, pos, fn) -> //Is in sync filled cache, try to get results from async filled cahces or calculate if it's not there
            let source =
                state.Files.TryFind fn
                |> Option.map (fun n -> n.Lines)
            match source with
            | None -> [CoreResponse.ErrorRes (sprintf "No help text available for symbol '%s'" sym)]
            | Some source ->
                let getSource = fun i -> source.[i - 1]

                let tip =
                    match state.HelpText.TryFind sym with
                    | None -> decl.DescriptionText
                    | Some tip -> tip
                state.HelpText.[sym] <- tip

                let n =
                    match state.CompletionNamespaceInsert.TryFind sym with
                    | None -> calculateNamespaceInser decl pos getSource
                    | Some s -> Some s
                [CoreResponse.HelpText (sym, tip, n)]




    member x.CompilerLocation () = [CoreResponse.CompilerLocation (Environment.fsc, Environment.fsi, Environment.msbuild, checker.GetDotnetRoot())]
    member x.Colorization enabled = state.ColorizationOutput <- enabled
    member x.Error msg = [CoreResponse.ErrorRes msg]

    member x.Completion (tyRes : ParseAndCheckResults) (pos: pos) lineStr (lines : string[]) (fileName : SourceFilePath) filter includeKeywords includeExternal =
        async {

            let fileName = Path.GetFullPath fileName
            let getAllSymbols () =
                if includeExternal then tyRes.GetAllEntities true else []
            let! res = tyRes.TryGetCompletions pos lineStr filter getAllSymbols
            return
                match res with
                | Some (decls, residue, shouldKeywords) ->
                    let declName (d: FSharpDeclarationListItem) = d.Name
                    let getLine = fun i -> lines.[i - 1]

                    //Init cache for current list
                    state.Declarations.Clear()
                    state.HelpText.Clear()
                    state.CompletionNamespaceInsert.Clear()
                    state.CurrentAST <- tyRes.GetAST

                    //Fill cache for current list
                    do fillHelpTextInTheBackground decls pos fileName getLine

                    // Send the first helptext without being requested.
                    // This allows it to be displayed immediately in the editor.
                    let firstMatchOpt =
                      decls
                      |> Array.sortBy declName
                      |> Array.tryFind (fun d -> (declName d).StartsWith(residue, StringComparison.InvariantCultureIgnoreCase))

                    let includeKeywords = includeKeywords && shouldKeywords

                    match firstMatchOpt with
                    | None -> [CoreResponse.Completion (decls, includeKeywords)]
                    | Some d ->
                        let insert = calculateNamespaceInser d pos getLine
                        [CoreResponse.HelpText (d.Name, d.DescriptionText, insert)
                         CoreResponse.Completion (decls, includeKeywords)]

                | None -> [CoreResponse.ErrorRes "Timed out while fetching completions"]
        }

    member x.ToolTip (tyRes : ParseAndCheckResults) (pos: pos) lineStr =
        tyRes.TryGetToolTipEnhanced pos lineStr
        |> x.MapResult CoreResponse.ToolTip
        |> x.AsCancellable (Path.GetFullPath tyRes.FileName)

    member x.FormattedDocumentation (tyRes : ParseAndCheckResults) (pos: pos) lineStr =
        tyRes.TryGetFormattedDocumentation pos lineStr
        |> x.MapResult CoreResponse.FormattedDocumentation

    member x.FormattedDocumentationForSymbol (tyRes : ParseAndCheckResults) (xmlSig: string) (assembly: string) =
        tyRes.TryGetFormattedDocumentationForSymbol xmlSig assembly
        |> x.MapResult CoreResponse.FormattedDocumentationForSymbol

    member x.Typesig (tyRes : ParseAndCheckResults) (pos: pos) lineStr =
        tyRes.TryGetToolTip pos lineStr
        |> x.MapResult CoreResponse.TypeSig
        |> x.AsCancellable (Path.GetFullPath tyRes.FileName)

    member x.SymbolUse (tyRes : ParseAndCheckResults) (pos: pos) lineStr =
        tyRes.TryGetSymbolUse pos lineStr
        |> x.MapResult CoreResponse.SymbolUse
        |> x.AsCancellable (Path.GetFullPath tyRes.FileName)

    member x.SignatureData (tyRes : ParseAndCheckResults) (pos: pos) lineStr =
        tyRes.TryGetSignatureData pos lineStr
        |> x.MapResult CoreResponse.SignatureData
        |> x.AsCancellable (Path.GetFullPath tyRes.FileName)

    member x.Help (tyRes : ParseAndCheckResults) (pos: pos) lineStr =
        tyRes.TryGetF1Help pos lineStr
        |> x.MapResult CoreResponse.Help
        |> x.AsCancellable (Path.GetFullPath tyRes.FileName)

    member x.SymbolUseProject (tyRes : ParseAndCheckResults) (pos: pos) lineStr =
        let fn = tyRes.FileName
        tyRes.TryGetSymbolUse pos lineStr |> x.MapResultAsync (fun (sym, usages) ->
            async {
                let fsym = sym.Symbol
                if fsym.IsPrivateToFile then
                    return CoreResponse.SymbolUse (sym, usages)
                elif backgroundServiceEnabled then
                    let! res =  SymbolCache.getSymbols fsym.FullName
                    match res with
                    | None ->
                        if fsym.IsInternalToProject then
                            let opts = state.FileCheckOptions.[tyRes.FileName]
                            let! symbols = checker.GetUsesOfSymbol (fn, [tyRes.FileName, opts] , sym.Symbol)
                            return CoreResponse.SymbolUse (sym, symbols)
                        else
                            let! symbols = checker.GetUsesOfSymbol (fn, state.FileCheckOptions.ToArray() |> Array.map (fun (KeyValue(k, v)) -> k,v) |> Seq.ofArray, sym.Symbol)
                            return CoreResponse.SymbolUse (sym, symbols)
                    | Some res ->
                        return CoreResponse.SymbolUseRange res
                elif fsym.IsInternalToProject then
                    let opts = state.FileCheckOptions.[tyRes.FileName]
                    let! symbols = checker.GetUsesOfSymbol (fn, [tyRes.FileName, opts] , sym.Symbol)
                    return CoreResponse.SymbolUse (sym, symbols)
                else
                    let! symbols = checker.GetUsesOfSymbol (fn, state.FileCheckOptions.ToArray() |> Array.map (fun (KeyValue(k, v)) -> k,v) |> Seq.ofArray, sym.Symbol)
                    return CoreResponse.SymbolUse (sym, symbols)
            })
        |> x.AsCancellable (Path.GetFullPath fn)

    member x.SymbolImplementationProject (tyRes : ParseAndCheckResults) (pos: pos) lineStr =
        let fn = tyRes.FileName
        let filterSymbols symbols =
            symbols
            |> Array.where (fun (su: FSharpSymbolUse) -> su.IsFromDispatchSlotImplementation || (su.IsFromType && not (UntypedAstUtils.isTypedBindingAtPosition tyRes.GetAST su.RangeAlternate )) )

        tyRes.TryGetSymbolUse pos lineStr |> x.MapResultAsync (fun (sym, usages) ->
            async {
                let fsym = sym.Symbol
                if fsym.IsPrivateToFile then
                    return CoreResponse.SymbolUseImplementation (sym, filterSymbols usages)
                elif backgroundServiceEnabled then
                    let! res =  SymbolCache.getImplementation fsym.FullName
                    match res with
                    | None ->
                        if fsym.IsInternalToProject then
                            let opts = state.FileCheckOptions.[tyRes.FileName]
                            let! symbols = checker.GetUsesOfSymbol (fn, [tyRes.FileName, opts] , sym.Symbol)
                            return CoreResponse.SymbolUseImplementation (sym, filterSymbols symbols )
                        else
                            let! symbols = checker.GetUsesOfSymbol (fn, state.FileCheckOptions.ToArray() |> Array.map (fun (KeyValue(k, v)) -> k,v) |> Seq.ofArray, sym.Symbol)
                            return CoreResponse.SymbolUseImplementation (sym, filterSymbols symbols)
                    | Some res ->
                        return CoreResponse.SymbolUseImplementationRange res
                elif fsym.IsInternalToProject then
                    let opts = state.FileCheckOptions.[tyRes.FileName]
                    let! symbols = checker.GetUsesOfSymbol (fn, [tyRes.FileName, opts] , sym.Symbol)
                    return CoreResponse.SymbolUseImplementation (sym, filterSymbols symbols )
                else
                    let! symbols = checker.GetUsesOfSymbol (fn, state.FileCheckOptions.ToArray() |> Array.map (fun (KeyValue(k, v)) -> k,v) |> Seq.ofArray, sym.Symbol)
                    let symbols = filterSymbols symbols
                    return CoreResponse.SymbolUseImplementation (sym, symbols )
            })
        |> x.AsCancellable (Path.GetFullPath fn)

    member x.FindDeclaration (tyRes : ParseAndCheckResults) (pos: pos) lineStr =
        tyRes.TryFindDeclaration pos lineStr
        |> x.MapResult (CoreResponse.FindDeclaration, CoreResponse.ErrorRes)
        |> x.AsCancellable (Path.GetFullPath tyRes.FileName)

    member x.FindTypeDeclaration (tyRes : ParseAndCheckResults) (pos: pos) lineStr =
        tyRes.TryFindTypeDeclaration pos lineStr
        |> x.MapResult (CoreResponse.FindTypeDeclaration, CoreResponse.ErrorRes)
        |> x.AsCancellable (Path.GetFullPath tyRes.FileName)

    member x.Methods (tyRes : ParseAndCheckResults) (pos: pos) (lines: LineStr[]) =
        tyRes.TryGetMethodOverrides lines pos
        |> x.MapResult (CoreResponse.Methods, CoreResponse.ErrorRes)
        |> x.AsCancellable (Path.GetFullPath tyRes.FileName)

    member x.Lint (file: SourceFilePath) =
        let file = Path.GetFullPath file
        async {
            match state.TryGetFileCheckerOptionsWithSource file with
            | Error s -> return [CoreResponse.ErrorRes s]
            | Ok (options, source) ->
                let tyResOpt = checker.TryGetRecentCheckResultsForFile(file, options)

                match tyResOpt with
                | None -> return [CoreResponse.InfoRes "Cached typecheck results not yet available"]
                | Some tyRes ->
                    match tyRes.GetAST with
                    | None -> return [CoreResponse.InfoRes "Something went wrong during parsing"]
                    | Some tree ->
                        try
                            let fsharpLintConfig = Lint.tryLoadConfiguration file
                            let opts =
                                match fsharpLintConfig with
                                | Ok config -> Some config
                                | Error _ -> None

                            match Lint.lintWithConfiguration opts tree source tyRes.GetCheckResults with
                            | Error e -> return [CoreResponse.InfoRes e]
                            | Ok enrichedWarnings ->
                                let res = CoreResponse.Lint (file, enrichedWarnings)
                                notify.Trigger (NotificationEvent.Lint res)
                                return [res]
                        with _ex ->
                            return [CoreResponse.InfoRes "Something went wrong during linter"]
        } |> x.AsCancellable file

    member x.GetNamespaceSuggestions (tyRes : ParseAndCheckResults) (pos: pos) (line: LineStr) =
        async {
            match tyRes.GetAST with
            | None -> return [CoreResponse.InfoRes "Parsed Tree not avaliable"]
            | Some parsedTree ->
            match Lexer.findLongIdents(pos.Column, line) with
            | None -> return [CoreResponse.InfoRes "Ident not found"]
            | Some (_,idents) ->
            match UntypedParseImpl.GetEntityKind(pos, parsedTree)  with
            | None -> return [CoreResponse.InfoRes "EntityKind not found"]
            | Some entityKind ->

            let symbol = Lexer.getSymbol pos.Line pos.Column line SymbolLookupKind.Fuzzy [||]
            match symbol with
            | None -> return [CoreResponse.InfoRes "Symbol at position not found"]
            | Some sym ->


            let entities = tyRes.GetAllEntities true

            let isAttribute = entityKind = EntityKind.Attribute
            let entities =
                entities |> List.filter (fun e ->
                    match entityKind, (e.Kind LookupType.Fuzzy) with
                    | EntityKind.Attribute, EntityKind.Attribute
                    | EntityKind.Type, (EntityKind.Type | EntityKind.Attribute)
                    | EntityKind.FunctionOrValue _, _ -> true
                    | EntityKind.Attribute, _
                    | _, EntityKind.Module _
                    | EntityKind.Module _, _
                    | EntityKind.Type, _ -> false)

            let maybeUnresolvedIdents =
                idents
                |> List.map (fun ident -> { Ident = ident; Resolved = false})
                |> List.toArray

            let entities =
                entities
                |> List.collect (fun e ->
                      [ yield e.TopRequireQualifiedAccessParent, e.AutoOpenParent, e.Namespace, e.CleanedIdents
                        if isAttribute then
                            let lastIdent = e.CleanedIdents.[e.CleanedIdents.Length - 1]
                            if (e.Kind LookupType.Fuzzy) = EntityKind.Attribute && lastIdent.EndsWith "Attribute" then
                                yield
                                    e.TopRequireQualifiedAccessParent,
                                    e.AutoOpenParent,
                                    e.Namespace,
                                    e.CleanedIdents
                                    |> Array.replace (e.CleanedIdents.Length - 1) (lastIdent.Substring(0, lastIdent.Length - 9)) ])
            let createEntity = ParsedInput.tryFindInsertionContext pos.Line parsedTree maybeUnresolvedIdents TopLevel
            let word = sym.Text
            let candidates = entities |> Seq.collect createEntity |> Seq.toList

            let openNamespace =
                candidates
                |> Seq.choose (fun (entity, ctx) -> entity.Namespace |> Option.map (fun ns -> ns, entity.Name, ctx))
                |> Seq.groupBy (fun (ns, _, _) -> ns)
                |> Seq.map (fun (ns, xs) ->
                    ns,
                    xs
                    |> Seq.map (fun (_, name, ctx) -> name, ctx)
                    |> Seq.distinctBy (fun (name, _) -> name)
                    |> Seq.sortBy fst
                    |> Seq.toArray)
                |> Seq.collect (fun (ns, names) ->
                    let multipleNames = names |> Array.length > 1
                    names |> Seq.map (fun (name, ctx) -> ns, name, ctx, multipleNames))
                |> Seq.toList

            let qualifySymbolActions =
                candidates
                |> Seq.map (fun (entity, _) -> entity.FullRelativeName, entity.Qualifier)
                |> Seq.distinct
                |> Seq.sort
                |> Seq.toList

            return [ CoreResponse.ResolveNamespaces (word, openNamespace, qualifySymbolActions) ]
        } |> x.AsCancellable (Path.GetFullPath tyRes.FileName)

    member x.GetUnionPatternMatchCases (tyRes : ParseAndCheckResults) (pos: pos) (lines: LineStr[]) (line: LineStr) =
        async {
            let codeGenService = CodeGenerationService(checker, state)
            let doc = {
                Document.LineCount = lines.Length
                FullName = tyRes.FileName
                GetText = fun _ -> lines |> String.concat "\n"
                GetLineText0 = fun i -> lines.[i]
                GetLineText1 = fun i -> lines.[i - 1]
            }

            let! res = tryFindUnionDefinitionFromPos codeGenService pos doc
            match res with
            | None -> return [CoreResponse.InfoRes "Union at position not found"]
            | Some (patMatchExpr, unionTypeDefinition, insertionPos) ->

            if shouldGenerateUnionPatternMatchCases patMatchExpr unionTypeDefinition then
                let result = formatMatchExpr insertionPos "$1" patMatchExpr unionTypeDefinition
                let pos = mkPos insertionPos.InsertionPos.Line insertionPos.InsertionPos.Column

                return [CoreResponse.UnionCase (result, pos) ]
            else
                return [CoreResponse.InfoRes "Union at position not found"]
        } |> x.AsCancellable (Path.GetFullPath tyRes.FileName)

    member x.GetRecordStub (tyRes : ParseAndCheckResults) (pos: pos) (lines: LineStr[]) (line: LineStr) =
        async {
            let codeGenServer = CodeGenerationService(checker, state)
            let doc = {
                Document.LineCount = lines.Length
                FullName = tyRes.FileName
                GetText = fun _ -> lines |> String.concat "\n"
                GetLineText0 = fun i -> lines.[i]
                GetLineText1 = fun i -> lines.[i - 1]
            }

            let! res = tryFindRecordDefinitionFromPos codeGenServer pos doc
            match res with
            | None -> return [CoreResponse.InfoRes "Record at position not found"]
            | Some(recordEpr, (Some recordDefinition), insertionPos) ->
                if shouldGenerateRecordStub recordEpr recordDefinition then
                    let result = formatRecord insertionPos "$1" recordDefinition recordEpr.FieldExprList
                    let pos = mkPos insertionPos.InsertionPos.Line insertionPos.InsertionPos.Column
                    return [CoreResponse.RecordStub (result, pos)]
                else
                    return [CoreResponse.InfoRes "Record at position not found"]
            | _ -> return [CoreResponse.InfoRes "Record at position not found"]
        } |> x.AsCancellable (Path.GetFullPath tyRes.FileName)

    member x.GetInterfaceStub (tyRes : ParseAndCheckResults) (pos: pos) (lines: LineStr[]) (lineStr: LineStr) =
        async {
            let codeGenServer = CodeGenerationService(checker, state)
            let doc = {
                Document.LineCount = lines.Length
                FullName = tyRes.FileName
                GetText = fun _ -> lines |> String.concat "\n"
                GetLineText0 = fun i -> lines.[i]
                GetLineText1 = fun i -> lines.[i - 1]
            }

            let! res = tryFindInterfaceExprInBufferAtPos codeGenServer pos doc
            match res with
            | None -> return [CoreResponse.InfoRes "Interface at position not found"]
            | Some interfaceData ->
                let! stubInfo = handleImplementInterface codeGenServer pos doc lines lineStr interfaceData

                match stubInfo with
                | Some (insertPosition, generatedCode) ->
                    return [CoreResponse.InterfaceStub (generatedCode, insertPosition)]
                | None -> return [CoreResponse.InfoRes "Interface at position not found"]
        } |> x.AsCancellable (Path.GetFullPath tyRes.FileName)

    member x.WorkspacePeek (dir: string) (deep: int) (excludedDirs: string list) = async {
        let d = WorkspacePeek.peek dir deep excludedDirs
        state.WorkspaceRoot <- dir

        return [CoreResponse.WorkspacePeek d]
    }

    member x.WorkspaceLoad onChange (files: string list) (disableInMemoryProjectReferences: bool) tfmForScripts = async {
        checker.DisableInMemoryProjectReferences <- disableInMemoryProjectReferences
        //TODO check full path
        let projectFileNames = files |> List.map Path.GetFullPath

        let projects =
            projectFileNames
            |> List.map (fun projectFileName -> projectFileName, new Project(projectFileName, onChange))

        for projectFileName, proj in projects do
            state.Projects.[projectFileName] <- proj

        let projectLoadedSuccessfully projectFileName response =
            let project =
                match state.Projects.TryFind projectFileName with
                | Some prj -> prj
                | None ->
                    let proj = new Project(projectFileName, onChange)
                    state.Projects.[projectFileName] <- proj
                    proj

            project.Response <- Some response

            onProjectLoaded projectFileName response

        let onLoaded p =
            match p with
            | WorkspaceProjectState.Loading projectFileName ->
                CoreResponse.ProjectLoading projectFileName
                |> NotificationEvent.Workspace
                |> notify.Trigger
            | WorkspaceProjectState.Loaded (opts, extraInfo, projectFiles, logMap) ->
                let projectFileName, response = x.ToProjectCache(opts, extraInfo, projectFiles, logMap)
                if backgroundServiceEnabled then BackgroundServices.updateProject(projectFileName, opts)
                projectLoadedSuccessfully projectFileName response tfmForScripts

                let responseFiles =
                    response.Items
                    |> List.choose (function Dotnet.ProjInfo.Workspace.ProjectViewerItem.Compile(p, _) -> Some p)

                CoreResponse.Project (projectFileName, responseFiles, response.OutFile, response.References, response.Log, response.ExtraInfo, projectFiles, Map.empty)
                |> NotificationEvent.Workspace
                |> notify.Trigger
            | WorkspaceProjectState.Failed (projectFileName, error) ->
                CoreResponse.ProjectError error
                |> NotificationEvent.Workspace
                |> notify.Trigger

        CoreResponse.WorkspaceLoad false
        |> NotificationEvent.Workspace
        |> notify.Trigger

        // this is to delay the project loading notification (of this thread)
        // after the workspaceload started response returned below in outer async
        // Make test output repeteable, and notification in correct order
        match Environment.workspaceLoadDelay() with
        | delay when delay > TimeSpan.Zero ->
            do! Async.Sleep(Environment.workspaceLoadDelay().TotalMilliseconds |> int)
        | _ -> ()

        let loader, fcsBinder = workspaceBinder ()

        let projViewer = Dotnet.ProjInfo.Workspace.ProjectViewer ()

        let bindNewOnloaded (n: Dotnet.ProjInfo.Workspace.WorkspaceProjectState) : WorkspaceProjectState option =
            match n with
            | Dotnet.ProjInfo.Workspace.WorkspaceProjectState.Loading (path, _) ->
                Some (WorkspaceProjectState.Loading path)
            | Dotnet.ProjInfo.Workspace.WorkspaceProjectState.Loaded (opts, logMap) ->
                match fcsBinder.GetProjectOptions(opts.ProjectFileName) with
                | Ok fcsOpts ->
                    match Workspace.extractOptionsDPW fcsOpts with
                    | Ok optsDPW ->
                        let view = projViewer.Render optsDPW
                        Some (WorkspaceProjectState.Loaded (fcsOpts, optsDPW.ExtraProjectInfo, view.Items, logMap))
                    | Error _ ->
                        None //TODO not ignore the error
                | Error _ ->
                    //TODO notify C# project too
                    None
            | Dotnet.ProjInfo.Workspace.WorkspaceProjectState.Failed (path, e) ->
                let error = e
                Some (WorkspaceProjectState.Failed (path, error))

        loader.Notifications.Add(fun (_, arg) ->
            arg |> bindNewOnloaded |> Option.iter onLoaded )

        do! Workspace.loadInBackground onLoaded (loader, fcsBinder) (projects |> List.map snd)

        CoreResponse.WorkspaceLoad true
        |> NotificationEvent.Workspace
        |> notify.Trigger

        x.IsWorkspaceReady <- true
        workspaceReady.Trigger ()


        return [CoreResponse.WorkspaceLoad true]
    }

    member x.GetUnusedDeclarations file =
        let file = Path.GetFullPath file
        let isScript = file.EndsWith ".fsx"

        async {
            match state.TryGetFileCheckerOptionsWithSource file with
            | Error s ->  return [CoreResponse.ErrorRes s]
            | Ok (opts, _) ->
                let tyResOpt = checker.TryGetRecentCheckResultsForFile(file, opts)
                match tyResOpt with
                | None -> return [ CoreResponse.InfoRes "Cached typecheck results not yet available"]
                | Some tyRes ->
                    let! allUses = tyRes.GetCheckResults.GetAllUsesOfAllSymbolsInFile ()
                    let unused = UnusedDeclarationsAnalyzer.getUnusedDeclarationRanges allUses isScript
                    let res = CoreResponse.UnusedDeclarations (file, unused)
                    notify.Trigger (NotificationEvent.UnusedDeclarations res)
                    return [ res ]
        } |> x.AsCancellable file

    member x.GetSimplifiedNames file =
        let file = Path.GetFullPath file
        async {
            match state.TryGetFileCheckerOptionsWithLines file with
            | Error s ->  return [CoreResponse.ErrorRes s]
            | Ok (opts, source) ->
                let tyResOpt = checker.TryGetRecentCheckResultsForFile(file, opts)
                match tyResOpt with
                | None -> return [ CoreResponse.InfoRes "Cached typecheck results not yet available"]
                | Some tyRes ->
                    let! allUses = tyRes.GetCheckResults.GetAllUsesOfAllSymbolsInFile ()
                    let! simplified = SimplifyNameDiagnosticAnalyzer.getSimplifyNameRanges tyRes.GetCheckResults source allUses
                    let res = CoreResponse.SimplifiedName (file, (Seq.toArray simplified))
                    notify.Trigger (NotificationEvent.SimplifyNames res)
                    return [ res ]
        } |> x.AsCancellable file

    member x.GetUnusedOpens file =
        let file = Path.GetFullPath file
        async {
            match state.TryGetFileCheckerOptionsWithLines file with
            | Error s ->  return [CoreResponse.ErrorRes s]
            | Ok (opts, source) ->
                let tyResOpt = checker.TryGetRecentCheckResultsForFile(file, opts)
                match tyResOpt with
                | None -> return [ CoreResponse.InfoRes "Cached typecheck results not yet available"]
                | Some tyRes ->
                    let! unused = UnusedOpens.getUnusedOpens(tyRes.GetCheckResults, fun i -> source.[i - 1])
                    let res = CoreResponse.UnusedOpens (file, (unused |> List.toArray))
                    notify.Trigger (NotificationEvent.UnusedOpens res)
                    return [ res ]
        } |> x.AsCancellable file


    member x.GetRangesAtPosition file positions =
        let file = Path.GetFullPath file
        let parseResult = state.ParseResults.TryFind file
        match parseResult with
        | None -> [ CoreResponse.InfoRes "Cached typecheck results not yet available"]
        | Some pr ->
            positions |> List.map (fun x ->
                UntypedAstUtils.getRangesAtPosition pr.ParseTree x
            )
            |> CoreResponse.RangesAtPositions
            |> List.singleton

    member x.Compile projectFileName = async {
        let projectFileName = Path.GetFullPath projectFileName
        match state.Projects.TryFind projectFileName with
        | None -> return [ CoreResponse.InfoRes "Project not found" ]
        | Some proj ->
        match proj.Response with
        | None -> return [ CoreResponse.InfoRes "Project not found" ]
        | Some proj ->
            let! errors,code = checker.Compile(proj.Options.OtherOptions)
            return [ CoreResponse.Compile (errors,code)]
    }

    member __.LoadAnalyzers (path: string) = async {
        let analyzers = Analyzers.loadAnalyzers path
        state.Analyzers.AddOrUpdate(path, (fun _ -> analyzers), (fun _ _ -> analyzers)) |> ignore
        return [CoreResponse.InfoRes (sprintf "%d Analyzers registered" analyzers.Length) ]
    }

    member __.StartBackgroundService (workspaceDir : string option) =
        if backgroundServiceEnabled && workspaceDir.IsSome then
            SymbolCache.initCache workspaceDir.Value
            BackgroundServices.start ()

    member __.ProcessProjectsInBackground (file) =
        if backgroundServiceEnabled then
            BackgroundServices.saveFile file

    member x.GetGitHash () =
        let version = Version.info ()
        version.GitSha

    member __.Quit () =
        async {
            return [ CoreResponse.InfoRes "quitting..." ]
        }

    member x.FakeTargets file ctx = async {
        let file = Path.GetFullPath file
        let! targets = FakeSupport.getTargets file ctx
        return [CoreResponse.FakeTargets targets]
    }

    member x.FakeRuntime () = async {
        let! runtimePath = FakeSupport.getFakeRuntime ()
        return [CoreResponse.FakeRuntime runtimePath]
    }

    member x.GetChecker () = checker.GetFSharpChecker()

    member x.ScopesForFile (file: string) = async {
        let file = Path.GetFullPath file
        match state.TryGetFileCheckerOptionsWithLines file with
        | Error s -> return Error s
        | Ok (opts, sourceLines) ->
            let parseOpts = Utils.projectOptionsToParseOptions opts
            let allSource = sourceLines |> String.concat "\n"
            let! ast = checker.ParseFile(file, allSource, parseOpts)
            match ast.ParseTree with
            | None -> return Error (ast.Errors |> Array.map string |> String.concat "\n")
            | Some ast' ->
                let ranges = Structure.getOutliningRanges sourceLines ast'
                return Ok ranges
    }

    member __.SetDotnetSDKRoot(path) = checker.SetDotnetRoot(path)
