namespace FsAutoComplete

open System

open FSharp.Compiler
open FSharp.Compiler.SourceCodeServices
open System.Text.RegularExpressions
open FSharp.Analyzers
open FsAutoComplete.WorkspacePeek
open SymbolCache

module internal CompletionUtils =
  let getIcon (glyph : FSharpGlyph) =
    match glyph with
    | FSharpGlyph.Class -> ("Class", "C")
    | FSharpGlyph.Constant -> ("Constant", "Cn")
    | FSharpGlyph.Delegate -> ("Delegate", "D")
    | FSharpGlyph.Enum -> ("Enum", "E")
    | FSharpGlyph.EnumMember -> ("Property", "P")
    | FSharpGlyph.Event -> ("Event", "e")
    | FSharpGlyph.Exception -> ("Exception", "X")
    | FSharpGlyph.Field -> ("Field", "F")
    | FSharpGlyph.Interface -> ("Interface", "I")
    | FSharpGlyph.Method -> ("Method", "M")
    | FSharpGlyph.OverridenMethod -> ("Method", "M")
    | FSharpGlyph.Module -> ("Module", "N")
    | FSharpGlyph.NameSpace -> ("Namespace", "N")
    | FSharpGlyph.Property -> ("Property", "P")
    | FSharpGlyph.Struct -> ("Struct", "S")
    | FSharpGlyph.Typedef -> ("Class", "C")
    | FSharpGlyph.Type -> ("Type", "T")
    | FSharpGlyph.Union -> ("Type", "T")
    | FSharpGlyph.Variable -> ("Variable", "V")
    | FSharpGlyph.ExtensionMethod -> ("Extension Method", "M")
    | FSharpGlyph.Error -> ("Error", "E")


  let getEnclosingEntityChar = function
    | FSharpEnclosingEntityKind.Namespace -> "N"
    | FSharpEnclosingEntityKind.Module -> "M"
    | FSharpEnclosingEntityKind.Class -> "C"
    | FSharpEnclosingEntityKind.Exception -> "E"
    | FSharpEnclosingEntityKind.Interface -> "I"
    | FSharpEnclosingEntityKind.Record -> "R"
    | FSharpEnclosingEntityKind.Enum -> "En"
    | FSharpEnclosingEntityKind.DU -> "D"

module CommandResponse =
  open FSharp.Compiler.Range

  type ResponseMsg<'T> =
    {
      Kind: string
      Data: 'T
    }

  type Location =
    {
      File: string
      Line: int
      Column: int
    }

  type CompletionResponse =
    {
      Name: string
      ReplacementText: string
      Glyph: string
      GlyphChar: string
      NamespaceToOpen: string option
    }

  type ResponseError<'T> =
    {
      Code: int
      Message: string
      AdditionalData: 'T
    }

  [<RequireQualifiedAccess>]
  type ErrorCodes =
    | GenericError = 1
    | ProjectNotRestored = 100
    | ProjectParsingFailed = 101
    | GenericProjectError = 102

  [<RequireQualifiedAccess>]
  type ErrorData =
    | GenericError
    | ProjectNotRestored of ProjectNotRestoredData
    | ProjectParsingFailed of ProjectParsingFailedData
    | GenericProjectError of ProjectData
  and ProjectNotRestoredData = { Project: ProjectFilePath }
  and ProjectParsingFailedData = { Project: ProjectFilePath }
  and ProjectData = { Project: ProjectFilePath }

  [<RequireQualifiedAccess>]
  type ProjectResponseInfo =
    | DotnetSdk of ProjectResponseInfoDotnetSdk
    | Verbose
    | ProjectJson
  and ProjectResponseInfoDotnetSdk =
    {
      IsTestProject: bool
      Configuration: string
      IsPackable: bool
      TargetFramework: string
      TargetFrameworkIdentifier: string
      TargetFrameworkVersion: string
      RestoreSuccess: bool
      TargetFrameworks: string list
      RunCmd: RunCmd option
      IsPublishable: bool option
    }
  and [<RequireQualifiedAccess>] RunCmd = { Command: string; Arguments: string }

  type ProjectLoadingResponse =
    {
      Project: ProjectFilePath
    }

  type ProjectResponse =
    {
      Project: ProjectFilePath
      Files: List<SourceFilePath>
      Output: string
      References: List<ProjectFilePath>
      Logs: Map<string, string>
      OutputType: ProjectOutputType
      Info: ProjectResponseInfo
      Items: List<ProjectResponseItem>
      AdditionalInfo: Map<string, string>
    }
  and ProjectOutputType =
    | Library
    | Exe
    | Custom of string
  and ProjectResponseItem =
    {
      Name: string
      FilePath: string
      VirtualPath: string
      Metadata: Map<string, string>
    }

  type OverloadDescription =
    {
      Signature: string
      Comment: string
    }

  type TooltipDescription =
    {
      Signature: string
      Comment: string
      Footer: string
    }

  type DocumentationDescription =
    {
      XmlKey: string
      Constructors: string list
      Fields: string list
      Functions: string list
      Interfaces: string list
      Attributes: string list
      Types: string list
      Signature: string
      Comment: string
      Footer: string
    }

  type OverloadParameter =
    {
      Name : string
      CanonicalTypeTextForSorting : string
      Display : string
      Description : string
    }
  type Overload =
    {
      Tip : OverloadDescription list list
      TypeText : string
      Parameters : OverloadParameter list
      IsStaticArguments : bool
    }
  type MethodResponse =
    {
      Name : string
      CurrentParameter : int
      Overloads : Overload list
    }

  type SymbolUseResponse =
    {
      Name: string
      Uses: SymbolCache.SymbolUseRange list
    }

  type AdditionalEdit =
    {
      Text: string
      Line: int
      Column: int
      Type: string
    }


  type HelpTextResponse =
    {
      Name: string
      Overloads: OverloadDescription list list
      AdditionalEdit: AdditionalEdit option
    }

  type CompilerLocationResponse =
    {
      Fsc: string option
      Fsi: string option
      MSBuild: string option
      SdkRoot: string option
    }

  type FSharpErrorInfo =
    {
      FileName: string
      StartLine:int
      EndLine:int
      StartColumn:int
      EndColumn:int
      Severity:FSharpErrorSeverity
      Message:string
      Subcategory:string
    }
    static member IsIgnored(e:FSharp.Compiler.SourceCodeServices.FSharpErrorInfo) =
        // FST-1027 support in Fake 5
        e.ErrorNumber = 213 && e.Message.StartsWith "'paket:"
    static member OfFSharpError(e:FSharp.Compiler.SourceCodeServices.FSharpErrorInfo) =
      {
        FileName = e.FileName
        StartLine = e.StartLineAlternate
        EndLine = e.EndLineAlternate
        StartColumn = e.StartColumn + 1
        EndColumn = e.EndColumn + 1
        Severity = e.Severity
        Message = e.Message
        Subcategory = e.Subcategory
      }

  type ErrorResponse =
    {
      File: string
      Errors: FSharpErrorInfo []
    }

  type Colorization =
    {
      Range: Range.range
      Kind: string
    }

  type Declaration =
    {
      UniqueName: string
      Name: string
      Glyph: string
      GlyphChar: string
      IsTopLevel: bool
      Range: Range.range
      BodyRange : Range.range
      File : string
      EnclosingEntity: string
      IsAbstract: bool
    }
    static member OfDeclarationItem(e:FSharpNavigationDeclarationItem, fn) =
      let (glyph, glyphChar) = CompletionUtils.getIcon e.Glyph
      {
        UniqueName = e.UniqueName
        Name = e.Name
        Glyph = glyph
        GlyphChar = glyphChar
        IsTopLevel = e.IsSingleTopLevel
        Range = e.Range
        BodyRange = e.BodyRange
        File = fn
        EnclosingEntity = CompletionUtils.getEnclosingEntityChar e.EnclosingEntityKind
        IsAbstract = e.IsAbstract
      }

  type DeclarationResponse = {
      Declaration : Declaration;
      Nested : Declaration []
  }

  type OpenNamespace = {
    Namespace : string
    Name : string
    Type : string
    Line : int
    Column : int
    MultipleNames : bool
  }

  type QualifySymbol = {
    Name : string
    Qualifier : string
  }

  type ResolveNamespaceResponse = {
    Opens : OpenNamespace []
    Qualifies: QualifySymbol []
    Word : string
  }

  type UnionCaseResponse = {
    Text : string
    Position : pos
  }

  type RecordStubResponse = {
      Text : string
      Position : pos
  }

  type InterfaceStubResponse = {
      Text : string
      Position : pos
  }

  type Parameter = {
    Name : string
    Type : string
  }

  type SignatureData = {
    OutputType : string
    Parameters : Parameter list list
    Generics : string list
  }

  type UnusedDeclaration = {
    Range: Range.range
    IsThisMember: bool
  }

  type UnusedDeclarations = {
    Declarations : UnusedDeclaration []
  }

  type UnusedOpens = {
    Declarations : Range.range []
  }

  type SimplifiedNameData = {
    RelativeName : string
    UnnecessaryRange: Range.range
  }

  type SimplifiedName = {
    Names: SimplifiedNameData []
  }

  type RangesAtPositions = {
    Ranges: Range.range list list
  }

  type WorkspacePeekResponse = {
    Found: WorkspacePeekFound list
  }
  and WorkspacePeekFound =
    | Directory of WorkspacePeekFoundDirectory
    | Solution of WorkspacePeekFoundSolution
  and WorkspacePeekFoundDirectory = {
    Directory: string
    Fsprojs: string list
  }
  and WorkspacePeekFoundSolution = {
    Path: string
    Items: WorkspacePeekFoundSolutionItem list
    Configurations: WorkspacePeekFoundSolutionConfiguration list
  }
  and [<RequireQualifiedAccess>] WorkspacePeekFoundSolutionItem = {
    Guid: Guid
    Name: string
    Kind: WorkspacePeekFoundSolutionItemKind
  }
  and WorkspacePeekFoundSolutionItemKind =
    | MsbuildFormat of WorkspacePeekFoundSolutionItemKindMsbuildFormat
    | Folder of WorkspacePeekFoundSolutionItemKindFolder
  and [<RequireQualifiedAccess>] WorkspacePeekFoundSolutionItemKindMsbuildFormat = {
    Configurations: WorkspacePeekFoundSolutionConfiguration list
  }
  and [<RequireQualifiedAccess>] WorkspacePeekFoundSolutionItemKindFolder = {
    Items: WorkspacePeekFoundSolutionItem list
    Files: FilePath list
  }
  and [<RequireQualifiedAccess>] WorkspacePeekFoundSolutionConfiguration = {
    Id: string
    ConfigurationName: string
    PlatformName: string
  }

  type WorkspaceLoadResponse = {
    Status: string
    }

  type CompileResponse = {
    Code: int
    Errors: FSharpErrorInfo []
  }

  type AnalyzerMsg =
    { Type: string
      Message: string
      Code: string
      Severity: string
      Range: Range.range
      Fixes: SDK.Fix list }
  type AnalyzerResponse =
    { File: string
      Messages: AnalyzerMsg list}

  type FsdnResponse = {
    Functions: string list
  }

  type DotnetNewListResponse = {
    Installed : DotnetNewTemplate.Template list
  }

  type DotnetNewGetDetailsResponse = {
    Detailed : DotnetNewTemplate.DetailedTemplate
  }

  type DotnetNewCreateCliResponse = {
    CommandName : string
    ParameterStr : string
  }

  let info (serialize : Serializer) (s: string) = serialize { Kind = "info"; Data = s }

  let errorG (serialize : Serializer) (errorData: ErrorData) message =
    let inline ser code data =
        serialize { Kind = "error"; Data = { Code = (int code); Message = message; AdditionalData = data }  }
    match errorData with
    | ErrorData.GenericError -> ser (ErrorCodes.GenericError) (obj())
    | ErrorData.GenericProjectError d -> ser (ErrorCodes.GenericProjectError) d
    | ErrorData.ProjectNotRestored d -> ser (ErrorCodes.ProjectNotRestored) d
    | ErrorData.ProjectParsingFailed d -> ser (ErrorCodes.ProjectParsingFailed) d

  let error (serialize : Serializer) (s: string) = errorG serialize ErrorData.GenericError s

  let helpText (serialize : Serializer) (name: string, tip: FSharpToolTipText, additionalEdit ) =
    let data = TipFormatter.formatTip tip |> List.map(List.map(fun (n,m) -> {OverloadDescription.Signature = n; Comment = m} ))
    let additionalEdit = additionalEdit |> Option.map (fun (n, l, c, t) -> {AdditionalEdit.Text = n; Line = l; Column = c; Type = t})
    serialize { Kind = "helptext"; Data = { HelpTextResponse.Name = name; Overloads = data; AdditionalEdit = additionalEdit  } }

  let helpTextSimple (serialize: Serializer) (name : string, tip: string) =
    let data = [[{OverloadDescription.Signature = name; Comment = tip}]]
    serialize {Kind = "helptext"; Data = {HelpTextResponse.Name = name; Overloads = data; AdditionalEdit = None} }

  let project (serialize : Serializer) (projectFileName, projectFiles, outFileOpt, references, logMap, (extra: Dotnet.ProjInfo.Workspace.ExtraProjectInfoData), projectItems: Dotnet.ProjInfo.Workspace.ProjectViewerItem list, additionals) =
    let projectInfo =
      match extra.ProjectSdkType with
      | Dotnet.ProjInfo.Workspace.ProjectSdkType.Verbose _ ->
        ProjectResponseInfo.Verbose
      | Dotnet.ProjInfo.Workspace.ProjectSdkType.DotnetSdk info ->
        ProjectResponseInfo.DotnetSdk {
          IsTestProject = info.IsTestProject
          Configuration = info.Configuration
          IsPackable = info.IsPackable
          TargetFramework = info.TargetFramework
          TargetFrameworkIdentifier = info.TargetFrameworkIdentifier
          TargetFrameworkVersion = info.TargetFrameworkVersion
          RestoreSuccess = info.RestoreSuccess
          TargetFrameworks = info.TargetFrameworks
          RunCmd =
            match info.RunCommand, info.RunArguments with
            | Some cmd, Some args -> Some { RunCmd.Command = cmd; Arguments = args }
            | Some cmd, None -> Some { RunCmd.Command = cmd; Arguments = "" }
            | _ -> None
          IsPublishable = info.IsPublishable
        }
    let mapItemResponse (p: Dotnet.ProjInfo.Workspace.ProjectViewerItem) : ProjectResponseItem =
      match p with
      | Dotnet.ProjInfo.Workspace.ProjectViewerItem.Compile (fullpath, extraInfo) ->
        { ProjectResponseItem.Name = "Compile"
          ProjectResponseItem.FilePath = fullpath
          ProjectResponseItem.VirtualPath = extraInfo.Link
          ProjectResponseItem.Metadata = Map.empty }

    let projectData =
      { Project = projectFileName
        Files = projectFiles
        Output = match outFileOpt with Some x -> x | None -> "null"
        References = List.sortBy IO.Path.GetFileName references
        Logs = logMap
        OutputType =
          match extra.ProjectOutputType with
          | Dotnet.ProjInfo.Workspace.ProjectOutputType.Library -> Library
          | Dotnet.ProjInfo.Workspace.ProjectOutputType.Exe -> Exe
          | Dotnet.ProjInfo.Workspace.ProjectOutputType.Custom outType -> Custom outType
        Info = projectInfo
        Items = projectItems |> List.map mapItemResponse
        AdditionalInfo = additionals }
    serialize { Kind = "project"; Data = projectData }

  let projectError (serialize : Serializer) errorDetails =
    let rec getMessageLines errorDetails =
      match errorDetails with
      | Dotnet.ProjInfo.Workspace.LanguageNotSupported (_) -> [sprintf "this project is not supported, only fsproj"]
      | Dotnet.ProjInfo.Workspace.ProjectNotLoaded (_) -> [sprintf "this project was not loaded due to some internal error"]
      | Dotnet.ProjInfo.Workspace.MissingExtraProjectInfos _ -> [sprintf "this project was not loaded because ExtraProjectInfos were missing"]
      | Dotnet.ProjInfo.Workspace.InvalidExtraProjectInfos (_, err) ->  [sprintf "this project was not loaded because ExtraProjectInfos were invalid: %s" err]
      | Dotnet.ProjInfo.Workspace.ReferencesNotLoaded (_, referenceErrors) ->

        [ yield sprintf "this project was not loaded because some references could not be loaded:"
          yield! 
            referenceErrors
              |> Seq.collect (fun (projPath, er) -> 
                [ yield sprintf "  - %s:" projPath 
                  yield! getMessageLines er |> Seq.map (fun line -> sprintf "    - %s" line)]) ]
      | Dotnet.ProjInfo.Workspace.GenericError (_, errorMessage) -> [errorMessage]
      | Dotnet.ProjInfo.Workspace.ProjectNotRestored _ -> ["Project not restored"]
    let msg = getMessageLines errorDetails |> fun s -> String.Join("\n", s)
    match errorDetails with
    | Dotnet.ProjInfo.Workspace.LanguageNotSupported (project) -> errorG serialize (ErrorData.GenericProjectError { Project = project }) msg
    | Dotnet.ProjInfo.Workspace.ProjectNotLoaded project -> errorG serialize (ErrorData.GenericProjectError { Project = project }) msg
    | Dotnet.ProjInfo.Workspace.MissingExtraProjectInfos project -> errorG serialize (ErrorData.GenericProjectError { Project = project }) msg
    | Dotnet.ProjInfo.Workspace.InvalidExtraProjectInfos (project, _) -> errorG serialize (ErrorData.GenericProjectError { Project = project }) msg
    | Dotnet.ProjInfo.Workspace.ReferencesNotLoaded (project, _) -> errorG serialize (ErrorData.GenericProjectError { Project = project }) msg
    | Dotnet.ProjInfo.Workspace.GenericError (project, _) -> errorG serialize (ErrorData.ProjectParsingFailed { Project = project }) msg
    | Dotnet.ProjInfo.Workspace.ProjectNotRestored project -> errorG serialize (ErrorData.ProjectNotRestored { Project = project }) msg

  let projectLoading (serialize : Serializer) projectFileName =
    serialize { Kind = "projectLoading"; Data = { ProjectLoadingResponse.Project = projectFileName } }

  let workspacePeek (serialize : Serializer) (found: FsAutoComplete.WorkspacePeek.Interesting list) =
    let mapInt i =
        match i with
        | Interesting.Directory (p, fsprojs) ->
            WorkspacePeekFound.Directory { WorkspacePeekFoundDirectory.Directory = p; Fsprojs = fsprojs }
        | Interesting.Solution (p, sd) ->
            let rec item (x: FsAutoComplete.WorkspacePeek.SolutionItem) =
                let kind =
                    match x.Kind with
                    | SolutionItemKind.Unknown
                    | SolutionItemKind.Unsupported ->
                        None
                    | SolutionItemKind.MsbuildFormat msbuildProj ->
                        Some (WorkspacePeekFoundSolutionItemKind.MsbuildFormat {
                            WorkspacePeekFoundSolutionItemKindMsbuildFormat.Configurations = []
                        })
                    | SolutionItemKind.Folder(children, files) ->
                        let c = children |> List.choose item
                        Some (WorkspacePeekFoundSolutionItemKind.Folder {
                            WorkspacePeekFoundSolutionItemKindFolder.Items = c
                            Files = files
                        })
                kind
                |> Option.map (fun k -> { WorkspacePeekFoundSolutionItem.Guid = x.Guid; Name = x.Name; Kind = k })
            let items = sd.Items |> List.choose item
            WorkspacePeekFound.Solution { WorkspacePeekFoundSolution.Path = p; Items = items; Configurations = [] }

    let data = { WorkspacePeekResponse.Found = found |> List.map mapInt }
    serialize { Kind = "workspacePeek"; Data = data }

  let workspaceLoad (serialize : Serializer) finished =
    let data =
        if finished then "finished" else "started"
    serialize { Kind = "workspaceLoad"; Data = { WorkspaceLoadResponse.Status = data } }

  let completion (serialize : Serializer) (decls: FSharpDeclarationListItem[]) includeKeywords =
      serialize {  Kind = "completion"
                   Data = [ for d in decls do
                              let code =
                                if Regex.IsMatch(d.Name, """^[a-zA-Z][a-zA-Z0-9']+$""") then d.Name else
                                PrettyNaming.QuoteIdentifierIfNeeded d.Name
                              let (glyph, glyphChar) = CompletionUtils.getIcon d.Glyph
                              yield {CompletionResponse.Name = d.Name; ReplacementText = code; Glyph = glyph; GlyphChar = glyphChar; NamespaceToOpen = d.NamespaceToOpen }
                            if includeKeywords then
                              for k in FsAutoComplete.KeywordList.allKeywords do
                                yield {CompletionResponse.Name = k; ReplacementText = k; Glyph = "Keyword"; GlyphChar = "K"; NamespaceToOpen = None}
                          ] }

  let symbolUse (serialize : Serializer) (symbol: FSharpSymbolUse, uses: FSharpSymbolUse[]) =
    let su =
      { Name = symbol.Symbol.DisplayName
        Uses =
          [ for su in uses do
              yield { StartLine = su.RangeAlternate.StartLine
                      StartColumn = su.RangeAlternate.StartColumn + 1
                      EndLine = su.RangeAlternate.EndLine
                      EndColumn = su.RangeAlternate.EndColumn + 1
                      FileName = su.FileName
                      IsFromDefinition = su.IsFromDefinition
                      IsFromAttribute = su.IsFromAttribute
                      IsFromComputationExpression = su.IsFromComputationExpression
                      IsFromDispatchSlotImplementation = su.IsFromDispatchSlotImplementation
                      IsFromPattern = su.IsFromPattern
                      IsFromType = su.IsFromType
                      SymbolFullName = symbol.Symbol.FullName
                      SymbolDisplayName = symbol.Symbol.DisplayName
                      SymbolIsLocal = symbol.Symbol.IsPrivateToFile } ] |> Seq.distinct |> Seq.toList }
    serialize { Kind = "symboluse"; Data = su }

  let symbolImplementation (serialize : Serializer) (symbol: FSharpSymbolUse, uses: FSharpSymbolUse[]) =
    let su =
      { Name = symbol.Symbol.DisplayName
        Uses =
          [ for su in uses do
              yield { StartLine = su.RangeAlternate.StartLine
                      StartColumn = su.RangeAlternate.StartColumn + 1
                      EndLine = su.RangeAlternate.EndLine
                      EndColumn = su.RangeAlternate.EndColumn + 1
                      FileName = su.FileName
                      IsFromDefinition = su.IsFromDefinition
                      IsFromAttribute = su.IsFromAttribute
                      IsFromComputationExpression = su.IsFromComputationExpression
                      IsFromDispatchSlotImplementation = su.IsFromDispatchSlotImplementation
                      IsFromPattern = su.IsFromPattern
                      IsFromType = su.IsFromType
                      SymbolFullName = symbol.Symbol.FullName
                      SymbolDisplayName = symbol.Symbol.DisplayName
                      SymbolIsLocal = symbol.Symbol.IsPrivateToFile } ] |> Seq.distinct |> Seq.toList }
    serialize { Kind = "symbolimplementation"; Data = su }

  let symbolUseRange (serialize : Serializer) (uses: SymbolUseRange[]) =
    let symbol = uses.[0]
    let su =
      { Name = symbol.SymbolDisplayName
        Uses = List.ofArray uses
      }
    serialize { Kind = "symboluse"; Data = su }

  let symbolUseImplementationRange (serialize : Serializer) (uses: SymbolUseRange[]) =
    let symbol = uses.[0]
    let su =
      { Name = symbol.SymbolDisplayName
        Uses = List.ofArray uses
      }
    serialize { Kind = "symbolimplementation"; Data = su }

  let signatureData (serialize : Serializer) ((typ, parms, generics) : string * ((string * string) list list) * string list) =
    let pms =
      parms
      |> List.map (List.map (fun (n, t) -> { Name= n; Type = t }))
    serialize { Kind = "signatureData"; Data = { Parameters = pms; OutputType = typ; Generics = generics } }

  let help (serialize : Serializer) (data : string) =
    serialize { Kind = "help"; Data = data }

  let fsdn (serialize : Serializer) (functions : string list) =
    let data = { FsdnResponse.Functions = functions }
    serialize { Kind = "fsdn"; Data = data }

  let dotnetnewlist (serialize : Serializer) (installedTemplate : DotnetNewTemplate.Template list) =
    let data = { DotnetNewListResponse.Installed = installedTemplate }
    serialize { Kind = "dotnetnewlist"; Data = data }

  let dotnetnewgetDetails (serialize : Serializer) (detailedTemplate : DotnetNewTemplate.DetailedTemplate) =
    let data = { DotnetNewGetDetailsResponse.Detailed = detailedTemplate }
    serialize { Kind = "dotnetnewgetDetails"; Data = data }

  let dotnetnewCreateCli (serialize : Serializer) (commandName : string, parameterStr : string) =
    serialize { Kind = "dotnetnewCreateCli"; 
                Data = { CommandName = commandName
                         ParameterStr = parameterStr} }

  let methods (serialize : Serializer) (meth: FSharpMethodGroup, commas: int) =
      serialize {  Kind = "method"
                   Data = {  Name = meth.MethodName
                             CurrentParameter = commas
                             Overloads =
                              [ for o in meth.Methods do
                                 let tip = TipFormatter.formatTip o.Description |> List.map(List.map(fun (n,m) -> {OverloadDescription.Signature = n; Comment = m} ))
                                 yield {
                                   Tip = tip
                                   TypeText = o.ReturnTypeText
                                   Parameters =
                                     [ for p in o.Parameters do
                                        yield {
                                          Name = p.ParameterName
                                          CanonicalTypeTextForSorting = p.CanonicalTypeTextForSorting
                                          Display = p.Display
                                          Description = "" //TODO: Investigate if Description is needed at all - not used in Ionide, check in Emacs and vim.
                                        }
                                   ]
                                   IsStaticArguments = not o.HasParameters
                                 }
                              ] }
                }

  let errors (serialize : Serializer) (errors: FSharp.Compiler.SourceCodeServices.FSharpErrorInfo[], file: string) =
    let errors =
        errors
        |> Array.filter (FSharpErrorInfo.IsIgnored >> not)
        |> Array.map FSharpErrorInfo.OfFSharpError
    serialize { Kind = "errors";
                Data = { File = file
                         Errors = errors }}

  let colorizations (serialize : Serializer) (colorizations: (Range.range * SemanticClassificationType)[]) =
    // let data = [ for r, k in colorizations do
    //                yield { Range = r; Kind = Enum.GetName(typeof<SemanticClassificationType>, k) } ]
    serialize { Kind = "colorizations"; Data = [] } //TODO: Fix colorization

  let findDeclaration (serialize : Serializer) (result: FindDeclarationResult) =
    match result with
    | FindDeclarationResult.Range range ->
        let data = { Line = range.StartLine; Column = range.StartColumn + 1; File = range.FileName }
        serialize { Kind = "finddecl"; Data = data }
    | FindDeclarationResult.ExternalDeclaration extDecl ->
        let data = { Line = extDecl.Line; Column = extDecl.Column + 1; File = extDecl.File }
        serialize { Kind = "finddecl"; Data = data }

  let findTypeDeclaration (serialize : Serializer) (range: Range.range) =
    let data = { Line = range.StartLine; Column = range.StartColumn + 1; File = range.FileName }
    serialize { Kind = "finddecl"; Data = data }

  let declarations (serialize : Serializer) (decls : (FSharpNavigationTopLevelDeclaration * string) []) =
     let decls' =
      decls |> Array.map (fun (d, fn) ->
        { Declaration = Declaration.OfDeclarationItem (d.Declaration, fn);
          Nested = d.Nested |> Array.map ( fun a -> Declaration.OfDeclarationItem(a,fn))
        })
     serialize { Kind = "declarations"; Data = decls' }

  let toolTip (serialize : Serializer) (tip, signature, footer, typeDoc) =
    let data = TipFormatter.formatTipEnhanced tip signature footer typeDoc |> List.map(List.map(fun (n,m,f) -> {Footer =f; Signature = n; Comment = m} ))
    serialize { Kind = "tooltip"; Data = data }

  let formattedDocumentation (serialize : Serializer) (tip, xmlSig, signature, footer, cn) =
    let data =
      match tip, xmlSig  with
      | Some tip, _ ->
        TipFormatter.formatDocumentation tip signature footer cn |> List.map(List.map(fun (n,cns, fds, funcs, intf, attrs,ts, m,f, cn) -> {XmlKey = cn; Constructors = cns |> Seq.toList; Fields = fds |> Seq.toList; Functions = funcs |> Seq.toList; Interfaces = intf |> Seq.toList; Attributes = attrs |> Seq.toList; Types = ts |> Seq.toList; Footer =f; Signature = n; Comment = m} ))
      | _, Some (xml, assembly) ->
        TipFormatter.formatDocumentationFromXmlSig xml assembly signature footer cn |> List.map(List.map(fun (n,cns, fds, funcs, intf, attrs,ts, m,f, cn) -> {XmlKey = cn; Constructors = cns |> Seq.toList; Fields = fds |> Seq.toList; Functions = funcs |> Seq.toList; Interfaces = intf |> Seq.toList; Attributes = attrs |> Seq.toList; Types = ts |> Seq.toList; Footer =f; Signature = n; Comment = m} ))
      | _ -> failwith "Shouldn't happen"
    serialize { Kind = "formattedDocumentation"; Data = data }

  let formattedDocumentationForSymbol (serialize : Serializer) xml assembly (xmlDoc : string list) (signature, footer, cn) =
    let data = TipFormatter.formatDocumentationFromXmlSig xml assembly signature footer cn |> List.map(List.map(fun (n,cns, fds, funcs, intf, attrs, ts, m,f, cn) ->
      let m = if String.IsNullOrWhiteSpace m then xmlDoc |> String.concat "\n" else m
      {XmlKey = cn; Constructors = cns |> Seq.toList; Fields = fds |> Seq.toList; Functions = funcs |> Seq.toList; Interfaces = intf |> Seq.toList; Attributes = attrs |> Seq.toList; Types = ts |> Seq.toList; Footer =f; Signature = n; Comment = m} ))
    serialize { Kind = "formattedDocumentation"; Data = data }

  let typeSig (serialize : Serializer) (tip) =
    let data = TipFormatter.extractSignature tip
    serialize { Kind = "typesig"; Data = data }

  let compilerLocation (serialize : Serializer) fsc fsi msbuild sdkRoot =
    let data = { Fsi = fsi; Fsc = fsc; MSBuild = msbuild; SdkRoot = sdkRoot }
    serialize { Kind = "compilerlocation"; Data = data }

  let message (serialize : Serializer) (kind: string, data: 'a) =
    serialize { Kind = kind; Data = data }

  let lint (serialize : Serializer) (warnings : Lint.EnrichedLintWarning list) =

    let data = warnings |> List.map (fun w -> w.Warning) |> List.toArray
    serialize { Kind = "lint"; Data = data }


  let resolveNamespace (serialize : Serializer) (word: string, opens : (string * string * InsertContext * bool) list, qualfies : (string * string) list) =
    let ops =
      opens
      |> List.map (fun (ns, name, ctx, multiple) ->
        {
          Namespace = ns
          Name = name
          Type = ctx.ScopeKind.ToString()
          Line = ctx.Pos.Line
          Column = ctx.Pos.Column
          MultipleNames = multiple
        })
      |> List.toArray

    let quals =
      qualfies
      |> List.map (fun (name, q) ->
        {
          QualifySymbol.Name = name
          QualifySymbol.Qualifier = q
        })
      |> List.toArray

    let data = {
      Opens = ops
      Qualifies = quals
      Word = word
    }

    serialize { Kind = "namespaces"; Data = data}

  let unionCase (serialize : Serializer) (text : string) position =
    let data : UnionCaseResponse = {
      Text = text
      Position = position
    }
    serialize { Kind = "unionCase"; Data = data}

  let recordStub (serialize : Serializer) (text : string) position =
    let data : RecordStubResponse = {
      Text = text
      Position = position
    }
    serialize { Kind = "recordStub"; Data = data}

  let interfaceStub (serialize : Serializer) (text : string) (position : pos) =
    let data : InterfaceStubResponse = {
      Text = text
      Position = position
    }
    serialize { Kind = "interfaceStub"; Data = data}

  let unusedDeclarations (serialize : Serializer) data =
    let data =
      data |> Array.map (fun (r, t) ->
        {
          UnusedDeclaration.Range = r
          IsThisMember = t
        })
    let data = {UnusedDeclarations.Declarations = data}
    serialize { Kind = "unusedDeclarations"; Data = data}

  let unusedOpens (serialize : Serializer) data =
    let data = {UnusedOpens.Declarations = data}
    serialize { Kind = "unusedOpens"; Data = data}

  let simplifiedNames (serialize : Serializer) data =
    let data = {
      SimplifiedName.Names = data |> Seq.map (fun (r,n) -> { SimplifiedNameData.RelativeName = n; SimplifiedNameData.UnnecessaryRange =r }) |> Seq.toArray
    }
    serialize { Kind = "simpifiedNames"; Data = data}

  let rangesAtPosition (serialize : Serializer) data =
    let data = {Ranges = data}
    serialize {Kind = "rangesAtPosition"; Data = data}

  let compile (serialize : Serializer) (errors,code) =
    serialize { Kind = "compile"; Data = {Code = code; Errors = Array.map FSharpErrorInfo.OfFSharpError errors}}

  let analyzer (serialize: Serializer) (messages: SDK.Message seq, file: string) =
    let r =
      messages |> Seq.map (fun m ->
        let s =
          match m.Severity with
          | SDK.Info -> "info"
          | SDK.Warning -> "warning"
          | SDK.Error -> "error"
        { Code = m.Code
          Fixes = m.Fixes
          Message = m.Message
          Severity = s
          Range = m.Range
          Type = m.Type
        })
      |> Seq.toList
    serialize { Kind = "analyzer"; Data = { File = file; Messages = r}}

  let fakeTargets (serialize : Serializer) (targets : FakeSupport.GetTargetsResult) =
     serialize targets

  let fakeRuntime (serialize : Serializer) (runtimePath : string) =
     serialize { Kind = "fakeRuntime"; Data = runtimePath }

  let serialize (s: Serializer) = function
    | CoreResponse.InfoRes(text) ->
      info s text
    | CoreResponse.ErrorRes(text) ->
      error s text
    | CoreResponse.HelpText(name, tip, additionalEdit) ->
      helpText s (name, tip, additionalEdit)
    | CoreResponse.HelpTextSimple(name, tip) ->
      helpTextSimple s (name, tip)
    | CoreResponse.Project(projectFileName, projectFiles, outFileOpt, references, logMap, extra, projectItems, additionals) ->
      project s (projectFileName, projectFiles, outFileOpt, references, logMap, extra, projectItems, additionals)
    | CoreResponse.ProjectError(errorDetails) ->
      projectError s errorDetails
    | CoreResponse.ProjectLoading(projectFileName) ->
      projectLoading s projectFileName
    | CoreResponse.WorkspacePeek(found) ->
      workspacePeek s found
    | CoreResponse.WorkspaceLoad(finished) ->
      workspaceLoad s finished
    | CoreResponse.Completion(decls, includeKeywords) ->
      completion s decls includeKeywords
    | CoreResponse.SymbolUse(symbol, uses) ->
      symbolUse s (symbol, uses)
    | CoreResponse.SymbolUseImplementation(symbol, uses) ->
      symbolImplementation s (symbol, uses)
    | CoreResponse.SignatureData(typ, parms, generics) ->
      signatureData s (typ, parms, generics)
    | CoreResponse.Help(data) ->
      help s data
    | CoreResponse.Methods(meth, commas) ->
      methods s (meth, commas)
    | CoreResponse.Errors(es, file) ->
      errors s (es, file)
    | CoreResponse.Colorizations(colors) ->
      colorizations s colors
    | CoreResponse.FindDeclaration(result) ->
      findDeclaration s result
    | CoreResponse.FindTypeDeclaration(range) ->
      findTypeDeclaration s range
    | CoreResponse.Declarations(decls) ->
      declarations s decls
    | CoreResponse.ToolTip(tip, signature, footer, typeDoc) ->
      toolTip s (tip, signature, footer, typeDoc)
    | CoreResponse.FormattedDocumentation(tip, xmlSig, signature, footer, cn) ->
      formattedDocumentation s (tip, xmlSig, signature, footer, cn)
    | CoreResponse.FormattedDocumentationForSymbol(xml, assembly, xmlDoc, signature, footer, cn) ->
      formattedDocumentationForSymbol s xml assembly xmlDoc (signature, footer, cn)
    | CoreResponse.TypeSig(tip) ->
      typeSig s tip
    | CoreResponse.CompilerLocation(fcs, fsi, msbuild, sdk) ->
      compilerLocation s fcs fsi msbuild sdk
    | CoreResponse.Lint(_,warnings) ->
      lint s warnings
    | CoreResponse.ResolveNamespaces(word, opens, qualifies) ->
      resolveNamespace s (word, opens, qualifies)
    | CoreResponse.UnionCase(text, position) ->
      unionCase s text position
    | CoreResponse.RecordStub(text, position) ->
      recordStub s text position
    | CoreResponse.UnusedDeclarations(_,decls) ->
      unusedDeclarations s decls
    | CoreResponse.UnusedOpens(_,opens) ->
      unusedOpens s opens
    | CoreResponse.SimplifiedName(_,names) ->
      simplifiedNames s names
    | CoreResponse.Compile(errors, code) ->
      compile s (errors, code)
    | CoreResponse.Analyzer(messages, file) ->
      analyzer s (messages, file)
    | CoreResponse.SymbolUseRange(ranges) ->
      symbolUseRange s ranges
    | CoreResponse.SymbolUseImplementationRange(ranges) ->
      symbolUseImplementationRange s ranges
    | CoreResponse.InterfaceStub(generatedCode, insertPosition) ->
      interfaceStub s generatedCode insertPosition
    | CoreResponse.RangesAtPositions(ranges) -> rangesAtPosition s ranges
    | CoreResponse.Fsdn(functions) -> fsdn s functions
    | CoreResponse.FakeTargets(targets) ->
      fakeTargets s targets
    | CoreResponse.FakeRuntime(runtimePath) ->
      fakeRuntime s runtimePath
    | CoreResponse.DotnetNewList (installedTemplate) -> dotnetnewlist s installedTemplate
    | CoreResponse.DotnetNewGetDetails (detailedTemplate) ->
      dotnetnewgetDetails s detailedTemplate
    | CoreResponse.DotnetNewCreateCli (commandName,parameterStr) -> dotnetnewCreateCli s (commandName, parameterStr)
