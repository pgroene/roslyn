﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Completion;

namespace Microsoft.CodeAnalysis.ExternalAccess.OmniSharp.Completion
{
    internal static class OmniSharpCompletionService
    {
        public static async ValueTask<bool> ShouldTriggerCompletionAsync(
            this CompletionService completionService,
            Document document,
            int caretPosition,
            CompletionTrigger trigger,
            ImmutableHashSet<string>? roles,
            OmniSharpCompletionOptions options,
            CancellationToken cancellationToken)
        {
            var text = await document.GetTextAsync(cancellationToken).ConfigureAwait(false);
            return completionService.ShouldTriggerCompletion(document.Project, document.Project.LanguageServices, text, caretPosition, trigger, options.ToCompletionOptions(), document.Project.Solution.Options, roles);
        }

        public static Task<CompletionList> GetCompletionsAsync(
           this CompletionService completionService,
           Document document,
           int caretPosition,
           CompletionTrigger trigger,
           ImmutableHashSet<string>? roles,
           OmniSharpCompletionOptions options,
           CancellationToken cancellationToken)
        {
            return completionService.GetCompletionsAsync(document, caretPosition, options.ToCompletionOptions(), document.Project.Solution.Options, trigger, roles, cancellationToken);
        }

        public static string? GetProviderName(this CompletionItem completionItem) => completionItem.ProviderName;
    }
}
