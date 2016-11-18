﻿using System;
using System.Composition;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;
using OmniSharp.Mef;
using OmniSharp.Models;
using OmniSharp.Options;
using OmniSharp.Roslyn.CSharp.Services.Documentation;

namespace OmniSharp.Roslyn.CSharp.Services.Types
{
    [OmniSharpHandler(OmnisharpEndpoints.TypeLookup, LanguageNames.CSharp)]
    public class TypeLookupService : RequestHandler<TypeLookupRequest, TypeLookupResponse>
    {
        private readonly FormattingOptions _formattingOptions;
        private readonly OmnisharpWorkspace _workspace;
        private static readonly SymbolDisplayFormat DefaultFormat = SymbolDisplayFormat.FullyQualifiedFormat.WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted);

        [ImportingConstructor]
        public TypeLookupService(OmnisharpWorkspace workspace, FormattingOptions formattingOptions)
        {
            _workspace = workspace;
            _formattingOptions = formattingOptions;
        }

        public async Task<TypeLookupResponse> Handle(TypeLookupRequest request)
        {
            var document = _workspace.GetDocument(request.FileName);
            var response = new TypeLookupResponse();
            if (document != null)
            {
                var semanticModel = await document.GetSemanticModelAsync();
                var sourceText = await document.GetTextAsync();
                var position = sourceText.Lines.GetPosition(new LinePosition(request.Line, request.Column));
                var symbol = await SymbolFinder.FindSymbolAtPositionAsync(semanticModel, position, _workspace);
                if (symbol != null)
                {
                    //non regular C# code semantics (interactive, script) don't allow namespaces
                    if (symbol.Kind == SymbolKind.NamedType)
                    {
                        if (document.SourceCodeKind == SourceCodeKind.Regular && !symbol.ContainingNamespace.IsGlobalNamespace)
                        {
                            response.Type = symbol.ToDisplayString(DefaultFormat);
                        }
                        else
                        {
                            response.Type = symbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
                        }
                    }
                    else
                    {
                        response.Type = symbol.ToMinimalDisplayString(semanticModel, position);
                    }
                }

                if (request.IncludeDocumentation)
                {
                    response.Documentation = DocumentationConverter.ConvertDocumentation(symbol.GetDocumentationCommentXml(), _formattingOptions.NewLine);
                }
            }

            return response;
        }
    }
}
