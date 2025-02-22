﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Classification.Classifiers;
using Microsoft.CodeAnalysis.Extensions;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.PooledObjects;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Classification
{
    public static class Classifier
    {
        public static async Task<IEnumerable<ClassifiedSpan>> GetClassifiedSpansAsync(
            Document document,
            TextSpan textSpan,
            CancellationToken cancellationToken = default)
        {
            var semanticModel = await document.GetRequiredSemanticModelAsync(cancellationToken).ConfigureAwait(false);

            // public options do not affect classification:
            return GetClassifiedSpans(document.Project.Solution.Workspace.Services, semanticModel, textSpan, ClassificationOptions.Default, cancellationToken);
        }

        /// <summary>
        /// Returns classified spans in ascending <see cref="ClassifiedSpan"/> order.
        /// <see cref="ClassifiedSpan"/>s may have the same <see cref="ClassifiedSpan.TextSpan"/>. This occurs when there are multiple
        /// <see cref="ClassifiedSpan.ClassificationType"/>s for the same region of code. For example, a reference to a static method
        /// will have two spans, one that designates it as a method, and one that designates it as static.
        /// <see cref="ClassifiedSpan"/>s may also have overlapping <see cref="ClassifiedSpan.TextSpan"/>s. This occurs when there are
        /// strings containing regex and/or escape characters.
        /// </summary>
        [Obsolete("Use GetClassifiedSpansAsync instead")]
        public static IEnumerable<ClassifiedSpan> GetClassifiedSpans(
            SemanticModel semanticModel,
            TextSpan textSpan,
            Workspace workspace,
            CancellationToken cancellationToken = default)
        {
            // public options do not affect classification:
            return GetClassifiedSpans(workspace.Services, semanticModel, textSpan, ClassificationOptions.Default, cancellationToken);
        }

        internal static IEnumerable<ClassifiedSpan> GetClassifiedSpans(
            HostWorkspaceServices workspaceServices,
            SemanticModel semanticModel,
            TextSpan textSpan,
            ClassificationOptions options,
            CancellationToken cancellationToken)
        {
            var service = workspaceServices.GetLanguageServices(semanticModel.Language).GetRequiredService<ISyntaxClassificationService>();

            var syntaxClassifiers = service.GetDefaultSyntaxClassifiers();

            var extensionManager = workspaceServices.GetRequiredService<IExtensionManager>();
            var getNodeClassifiers = extensionManager.CreateNodeExtensionGetter(syntaxClassifiers, c => c.SyntaxNodeTypes);
            var getTokenClassifiers = extensionManager.CreateTokenExtensionGetter(syntaxClassifiers, c => c.SyntaxTokenKinds);

            using var _1 = ArrayBuilder<ClassifiedSpan>.GetInstance(out var syntacticClassifications);
            using var _2 = ArrayBuilder<ClassifiedSpan>.GetInstance(out var semanticClassifications);

            var root = semanticModel.SyntaxTree.GetRoot(cancellationToken);

            service.AddSyntacticClassifications(root, textSpan, syntacticClassifications, cancellationToken);
            service.AddSemanticClassifications(semanticModel, textSpan, getNodeClassifiers, getTokenClassifiers, semanticClassifications, options, cancellationToken);

            var allClassifications = new List<ClassifiedSpan>(semanticClassifications.Where(s => s.TextSpan.OverlapsWith(textSpan)));
            var semanticSet = semanticClassifications.Select(s => s.TextSpan).ToSet();

            allClassifications.AddRange(syntacticClassifications.Where(
                s => s.TextSpan.OverlapsWith(textSpan) && !semanticSet.Contains(s.TextSpan)));
            allClassifications.Sort((s1, s2) => s1.TextSpan.Start - s2.TextSpan.Start);

            return allClassifications;
        }

        internal static async Task<ImmutableArray<SymbolDisplayPart>> GetClassifiedSymbolDisplayPartsAsync(
            HostWorkspaceServices workspaceServices, SemanticModel semanticModel, TextSpan textSpan, ClassificationOptions options,
            CancellationToken cancellationToken = default)
        {
            var classifiedSpans = GetClassifiedSpans(workspaceServices, semanticModel, textSpan, options, cancellationToken);
            var sourceText = await semanticModel.SyntaxTree.GetTextAsync(cancellationToken).ConfigureAwait(false);

            return ConvertClassificationsToParts(sourceText, textSpan.Start, classifiedSpans);
        }

        internal static ImmutableArray<SymbolDisplayPart> ConvertClassificationsToParts(
            SourceText sourceText, int startPosition, IEnumerable<ClassifiedSpan> classifiedSpans)
        {
            var parts = ArrayBuilder<SymbolDisplayPart>.GetInstance();

            foreach (var span in classifiedSpans)
            {
                // If there is space between this span and the last one, then add a space.
                if (startPosition < span.TextSpan.Start)
                {
                    parts.AddRange(Space());
                }

                var kind = GetClassificationKind(span.ClassificationType);
                if (kind != null)
                {
                    parts.Add(new SymbolDisplayPart(kind.Value, null, sourceText.ToString(span.TextSpan)));

                    startPosition = span.TextSpan.End;
                }
            }

            return parts.ToImmutableAndFree();
        }

        private static IEnumerable<SymbolDisplayPart> Space(int count = 1)
        {
            yield return new SymbolDisplayPart(SymbolDisplayPartKind.Space, null, new string(' ', count));
        }

        private static SymbolDisplayPartKind? GetClassificationKind(string type)
            => type switch
            {
                ClassificationTypeNames.Identifier => SymbolDisplayPartKind.Text,
                ClassificationTypeNames.Keyword => SymbolDisplayPartKind.Keyword,
                ClassificationTypeNames.NumericLiteral => SymbolDisplayPartKind.NumericLiteral,
                ClassificationTypeNames.StringLiteral => SymbolDisplayPartKind.StringLiteral,
                ClassificationTypeNames.WhiteSpace => SymbolDisplayPartKind.Space,
                ClassificationTypeNames.Operator => SymbolDisplayPartKind.Operator,
                ClassificationTypeNames.Punctuation => SymbolDisplayPartKind.Punctuation,
                ClassificationTypeNames.ClassName => SymbolDisplayPartKind.ClassName,
                ClassificationTypeNames.RecordClassName => SymbolDisplayPartKind.RecordClassName,
                ClassificationTypeNames.StructName => SymbolDisplayPartKind.StructName,
                ClassificationTypeNames.InterfaceName => SymbolDisplayPartKind.InterfaceName,
                ClassificationTypeNames.DelegateName => SymbolDisplayPartKind.DelegateName,
                ClassificationTypeNames.EnumName => SymbolDisplayPartKind.EnumName,
                ClassificationTypeNames.TypeParameterName => SymbolDisplayPartKind.TypeParameterName,
                ClassificationTypeNames.ModuleName => SymbolDisplayPartKind.ModuleName,
                ClassificationTypeNames.VerbatimStringLiteral => SymbolDisplayPartKind.StringLiteral,
                ClassificationTypeNames.FieldName => SymbolDisplayPartKind.FieldName,
                ClassificationTypeNames.EnumMemberName => SymbolDisplayPartKind.EnumMemberName,
                ClassificationTypeNames.ConstantName => SymbolDisplayPartKind.ConstantName,
                ClassificationTypeNames.LocalName => SymbolDisplayPartKind.LocalName,
                ClassificationTypeNames.ParameterName => SymbolDisplayPartKind.ParameterName,
                ClassificationTypeNames.ExtensionMethodName => SymbolDisplayPartKind.ExtensionMethodName,
                ClassificationTypeNames.MethodName => SymbolDisplayPartKind.MethodName,
                ClassificationTypeNames.PropertyName => SymbolDisplayPartKind.PropertyName,
                ClassificationTypeNames.LabelName => SymbolDisplayPartKind.LabelName,
                ClassificationTypeNames.NamespaceName => SymbolDisplayPartKind.NamespaceName,
                ClassificationTypeNames.EventName => SymbolDisplayPartKind.EventName,
                _ => null,
            };
    }
}
