namespace CodeLens

open System
open System.Windows.Controls
open System.Windows.Media
open Microsoft.VisualStudio.Text
open Microsoft.VisualStudio.Text.Editor
open Microsoft.VisualStudio.Text.Formatting
open Microsoft.VisualStudio.Text.Tagging
open System.ComponentModel.Composition
open System.Collections
open Microsoft.VisualStudio.Utilities
open System.Windows.Media.Animation
open System
open Microsoft.FSharp.Compiler.SourceCodeServices
open Microsoft.FSharp.Compiler.Ast
open System.Windows
open System.Threading

module internal FSharpConstants =
    
    [<Literal>]
    /// "871D2A70-12A2-4e42-9440-425DD92A4116"
    let packageGuidString = "871D2A70-12A2-4e42-9440-425DD92A4116"
    
    [<Literal>]
    /// "BC6DD5A5-D4D6-4dab-A00D-A51242DBAF1B"
    let languageServiceGuidString = "BC6DD5A5-D4D6-4dab-A00D-A51242DBAF1B"
    
    [<Literal>]
    /// "4EB7CCB7-4336-4FFD-B12B-396E9FD079A9"
    let editorFactoryGuidString = "4EB7CCB7-4336-4FFD-B12B-396E9FD079A9"
    
    [<Literal>]
    /// "F#"
    let FSharpLanguageName = "F#"
    
    [<Literal>]
    /// "F#"
    let FSharpContentTypeName = "F#"

    [<Literal>]
    /// "F# Signature Help"
    let FSharpSignatureHelpContentTypeName = "F# Signature Help"
    
    [<Literal>]
    /// "F# Language Service"
    let FSharpLanguageServiceCallbackName = "F# Language Service"
    
    [<Literal>]
    /// "FSharp"
    let FSharpLanguageLongName = "FSharp"

type internal CodeLensAdornment(view:IWpfTextView, textDocumentFactory:ITextDocumentFactoryService, checker:FSharpChecker) as self =
    
    let codeLensLines = System.Collections.Generic.Dictionary()

    let run a = Async.Start a

    /// Text view where the adornment is created.
    let view = view
    //do view.TextBuffer.Changed.AddHandler(fun _ e -> self.OnTextChanged e)
    let txtDocumentF = textDocumentFactory

    let document = 
        let mutable document : ITextDocument = null
        let t = txtDocumentF
        let buffer = view.TextBuffer
        t.TryGetTextDocument(buffer, &document) |> ignore
        document

    do assert (document <> null)

    let filePath = document.FilePath
    let checker = checker

    let mutable cts = new CancellationTokenSource();
    let mutable ct = cts.Token


    /// Get the interline layer. CodeLens belong there.
    let interlineLayer = view.GetAdornmentLayer(PredefinedAdornmentLayers.InterLine)
    do view.LayoutChanged.AddHandler (fun _ e -> self.OnLayoutChanged e)
    
    let executeCodeLenseAsync () =
        async{
            try 
                let source = view.TextSnapshot.GetText()
                let! (options, _) =
                     checker.GetProjectOptionsFromScript(filePath, source)

                let! _, checkFileResults = 
                    checker.GetBackgroundCheckResultsForFileInProject(filePath, options)
                
                let! symbolUses = checkFileResults.GetAllUsesOfAllSymbolsInFile()

                let applyCodeLens bufferPosition text =
                    let DoUI () = 
                        try
                            let line = view.TextViewLines.GetTextViewLineContainingBufferPosition(bufferPosition)
                            
                            let offset = 
                                [0..line.Length - 1] |> Seq.tryFind (fun i -> not (Char.IsWhiteSpace (line.Start.Add(i).GetChar())))
                                |> Option.defaultValue 0

                            let realStart = line.Start.Add(offset)
                            let span = SnapshotSpan(line.Snapshot, Span.FromBounds(int realStart, int line.End))
                            let geometry = view.TextViewLines.GetMarkerGeometry(span)
                            let textBox = TextBlock(Width = 500., Background = Brushes.Transparent, Opacity = 0.5, Text = text)
                            Canvas.SetLeft(textBox, geometry.Bounds.Left)
                            Canvas.SetTop(textBox, geometry.Bounds.Top - 15.)
                            interlineLayer.AddAdornment(AdornmentPositioningBehavior.TextRelative, Nullable (span), null, textBox, null) |> ignore
                            if line.VisibilityState = VisibilityState.Unattached then view.DisplayTextLineContainingBufferPosition(line.Start, 0., ViewRelativePosition.Top)
                        with
                        | _ -> ()
                    
                    Application.Current.Dispatcher.Invoke(fun _ -> DoUI())
                
                let useResults (displayContext: FSharpDisplayContext, func: FSharpMemberOrFunctionOrValue) =
                    try
                        let lineNumber = func.DeclarationLocation.StartLine
                        
                        if lineNumber >= 0 || lineNumber < view.TextSnapshot.LineCount then
                            let typeName = func.FullType.Format(displayContext)
                            let bufferPosition = view.TextSnapshot.GetLineFromLineNumber(lineNumber).Start
                            if not (codeLensLines.ContainsKey lineNumber) then 
                                codeLensLines.[lineNumber] <- typeName
                                applyCodeLens bufferPosition typeName
                    with
                    | _ -> () // supress any exception according wrong line numbers -.-
                
                //let forceReformat () =
                //    view.VisualSnapshot.Lines
                //    |> Seq.iter(fun line -> view.DisplayTextLineContainingBufferPosition(line.Start, 25., ViewRelativePosition.Top))

                for symbolUse in symbolUses do
                    if symbolUse.IsFromDefinition then
                        match symbolUse.Symbol with
                        | :? FSharpMemberOrFunctionOrValue as func -> useResults (symbolUse.DisplayContext, func)
                        | _ -> ()
                        
                Application.Current.Dispatcher.Invoke(Action(fun _ -> view.VisualElement.InvalidateArrange()))
            with
            | _ -> () // TODO: Should report error
        }

    let mutable currentAsync = executeCodeLenseAsync ()

    /// Handles required transformation depending on whether CodeLens are required or not required
    interface ILineTransformSource with
        override t.GetLineTransform(line, _, _) =
            let applyCodeLens = codeLensLines.ContainsKey(view.TextSnapshot.GetLineNumberFromPosition(line.Start.Position))
            if applyCodeLens then
                // Give us space for CodeLens
                LineTransform(15., 1., 1.)
            else
                //Restore old transformation
                line.DefaultLineTransform

    member self.OnLayoutChanged (e:TextViewLayoutChangedEventArgs) =
        //Non expensive computations which have to be done immediate
        let newSnapshot = e.NewSnapshot
        e.NewOrReformattedLines
            |> Seq.iter (fun line -> 
                            let lineNumber = view.TextSnapshot.GetLineNumberFromPosition(line.Start.Position)
                            codeLensLines.Remove(lineNumber) |> ignore) //All changed lines are supposed to be now No-CodeLens-Lines (Reset)

        view.TextViewLines.WpfTextViewLines
            |> Seq.iter (fun line -> 
                            if line.VisibilityState = VisibilityState.Unattached then 
                                view.DisplayTextLineContainingBufferPosition(line.Start, 0., ViewRelativePosition.Top)) //Force refresh (works partly...)
        
        try
            cts.Cancel() // Stop all ongoing async workflow. 
            cts.Dispose()
            cts <- new CancellationTokenSource()
            ct <- cts.Token
            Async.Start (executeCodeLenseAsync(), ct)
        with
        | _ -> ()

    

[<Export(typeof<ILineTransformSourceProvider>); ContentType(FSharpConstants.FSharpContentTypeName); TextViewRole(PredefinedTextViewRoles.Document)>]
type internal CodeLensProvider 
    () as self =
    let TextAdornments = Collections.Generic.List<IWpfTextView * CodeLensAdornment>()
    
    let checker = FSharpChecker.Create()
    
    /// Returns an provider for the textView if already one has been created.
    /// Else create one.
    let getSuitableAdornmentProvider (textView:IWpfTextView) =
        let res = TextAdornments |> Seq.tryFind(fun (view, _) -> view = textView)
        match res with
        | Some (_, res) -> res
        | None -> 
            let provider = CodeLensAdornment(textView, self.TextDocumentFactory, checker)
            TextAdornments.Add((textView, provider))
            provider

    [<Import>]
    member val TextDocumentFactory : ITextDocumentFactoryService = null with get, set
    

    interface ILineTransformSourceProvider with
        override t.Create textView = 
            getSuitableAdornmentProvider(textView) :> ILineTransformSource
            

     