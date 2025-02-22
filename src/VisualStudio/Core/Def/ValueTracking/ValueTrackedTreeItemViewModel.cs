﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Immutable;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.Navigation;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.ValueTracking;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.Shell;
using Microsoft.CodeAnalysis.Classification;

namespace Microsoft.VisualStudio.LanguageServices.ValueTracking
{
    internal sealed class ValueTrackedTreeItemViewModel : TreeItemViewModel
    {
        private bool _childrenCalculated;
        private readonly Solution _solution;
        private readonly IGlyphService _glyphService;
        private readonly IValueTrackingService _valueTrackingService;
        private readonly ValueTrackedItem _trackedItem;
        private readonly IGlobalOptionService _globalOptions;

        public override bool IsNodeExpanded
        {
            get => base.IsNodeExpanded;
            set
            {
                base.IsNodeExpanded = value;
                CalculateChildren();
            }
        }

        private ValueTrackedTreeItemViewModel(
            ValueTrackedItem trackedItem,
            ImmutableArray<ClassifiedSpan> classifiedSpans,
            Solution solution,
            ValueTrackingTreeViewModel treeViewModel,
            IGlyphService glyphService,
            IValueTrackingService valueTrackingService,
            IGlobalOptionService globalOptions,
            IThreadingContext threadingContext,
            string fileName,
            ImmutableArray<TreeItemViewModel> children)
            : base(
                  trackedItem.Span,
                  trackedItem.SourceText,
                  trackedItem.DocumentId,
                  fileName,
                  trackedItem.Glyph,
                  classifiedSpans,
                  treeViewModel,
                  glyphService,
                  threadingContext,
                  solution.Workspace,
                  children)
        {

            _trackedItem = trackedItem;
            _solution = solution;
            _glyphService = glyphService;
            _valueTrackingService = valueTrackingService;
            _globalOptions = globalOptions;

            if (children.IsEmpty)
            {
                // Add an empty item so the treeview has an expansion showing to calculate
                // the actual children of the node
                ChildItems.Add(EmptyTreeViewItem.Instance);

                ChildItems.CollectionChanged += (s, a) =>
                {
                    NotifyPropertyChanged(nameof(ChildItems));
                };
            }
        }

        internal static async ValueTask<TreeItemViewModel> CreateAsync(
            Solution solution,
            ValueTrackedItem item,
            ImmutableArray<TreeItemViewModel> children,
            ValueTrackingTreeViewModel treeViewModel,
            IGlyphService glyphService,
            IValueTrackingService valueTrackingService,
            IGlobalOptionService globalOptions,
            IThreadingContext threadingContext,
            CancellationToken cancellationToken)
        {
            var document = solution.GetRequiredDocument(item.DocumentId);
            var fileName = document.FilePath ?? document.Name;

            var options = globalOptions.GetClassificationOptions(document.Project.Language);
            var documentSpan = await ClassifiedSpansAndHighlightSpanFactory.GetClassifiedDocumentSpanAsync(document, item.Span, options, cancellationToken).ConfigureAwait(false);
            var classificationResult = await ClassifiedSpansAndHighlightSpanFactory.ClassifyAsync(documentSpan, options, cancellationToken).ConfigureAwait(false);
            var classifiedSpans = classificationResult.ClassifiedSpans;

            return new ValueTrackedTreeItemViewModel(
                item,
                classifiedSpans,
                solution,
                treeViewModel,
                glyphService,
                valueTrackingService,
                globalOptions,
                threadingContext,
                fileName,
                children);
        }

        private void CalculateChildren()
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            if (_childrenCalculated || IsLoading)
            {
                return;
            }

            TreeViewModel.LoadingCount++;
            IsLoading = true;
            ChildItems.Clear();

            var computingItem = new ComputingTreeViewItem();
            ChildItems.Add(computingItem);

            Task.Run(async () =>
            {
                try
                {
                    var children = await CalculateChildrenAsync(ThreadingContext.DisposalToken).ConfigureAwait(false);

                    await ThreadingContext.JoinableTaskFactory.SwitchToMainThreadAsync();

                    ChildItems.Clear();

                    foreach (var child in children)
                    {
                        ChildItems.Add(child);
                    }
                }
                finally
                {
                    await ThreadingContext.JoinableTaskFactory.SwitchToMainThreadAsync();
                    TreeViewModel.LoadingCount--;
                    _childrenCalculated = true;
                    IsLoading = false;
                }
            }, ThreadingContext.DisposalToken);
        }

        public override void NavigateTo()
        {
            var navigationService = Workspace.Services.GetService<IDocumentNavigationService>();
            if (navigationService is null)
            {
                return;
            }

            // While navigating do not activate the tab, which will change focus from the tool window
            var options = new NavigationOptions(PreferProvisionalTab: true, ActivateTab: false);
            navigationService.TryNavigateToSpan(Workspace, DocumentId, _trackedItem.Span, options, ThreadingContext.DisposalToken);
        }

        private async Task<ImmutableArray<TreeItemViewModel>> CalculateChildrenAsync(CancellationToken cancellationToken)
        {
            var valueTrackedItems = await _valueTrackingService.TrackValueSourceAsync(
                _solution,
                _trackedItem,
                cancellationToken).ConfigureAwait(false);

            return await valueTrackedItems.SelectAsArrayAsync((item, cancellationToken) =>
                CreateAsync(_solution, item, children: ImmutableArray<TreeItemViewModel>.Empty, TreeViewModel, _glyphService, _valueTrackingService, _globalOptions, ThreadingContext, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
    }
}
