﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Threading;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis
{
    /// <summary>
    /// Input nodes are the 'root' nodes in the graph, and get their values from the inputs of the driver state table
    /// </summary>
    /// <typeparam name="T">The type of the input</typeparam>
    internal sealed class InputNode<T> : IIncrementalGeneratorNode<T>
    {
        private readonly Func<DriverStateTable.Builder, ImmutableArray<T>> _getInput;
        private readonly Action<IIncrementalGeneratorOutputNode> _registerOutput;
        private readonly IEqualityComparer<T> _inputComparer;
        private readonly IEqualityComparer<T> _comparer;
        private readonly string? _name;

        public InputNode(Func<DriverStateTable.Builder, ImmutableArray<T>> getInput, IEqualityComparer<T>? inputComparer = null)
            : this(getInput, registerOutput: null, inputComparer: inputComparer, comparer: null)
        {
        }

        private InputNode(Func<DriverStateTable.Builder, ImmutableArray<T>> getInput, Action<IIncrementalGeneratorOutputNode>? registerOutput, IEqualityComparer<T>? inputComparer = null, IEqualityComparer<T>? comparer = null, string? name = null)
        {
            _getInput = getInput;
            _comparer = comparer ?? EqualityComparer<T>.Default;
            _inputComparer = inputComparer ?? EqualityComparer<T>.Default;
            _registerOutput = registerOutput ?? (o => throw ExceptionUtilities.Unreachable);
            _name = name;
        }

        public NodeStateTable<T> UpdateStateTable(DriverStateTable.Builder graphState, NodeStateTable<T> previousTable, CancellationToken cancellationToken)
        {
            var stopwatch = SharedStopwatch.StartNew();
            var inputItems = _getInput(graphState);
            TimeSpan elapsedTime = stopwatch.Elapsed;

            // create a mutable hashset of the new items we can check against
            HashSet<T> itemsSet = new HashSet<T>(_inputComparer);
            foreach (var item in inputItems)
            {
                var added = itemsSet.Add(item);
                Debug.Assert(added);
            }

            var builder = graphState.CreateTableBuilder(previousTable, _name);

            // We always have no inputs steps into an InputNode, but we track the difference between "no inputs" (empty collection) and "no step information" (default value)
            var noInputStepsStepInfo = builder.TrackIncrementalSteps ? ImmutableArray<(IncrementalGeneratorRunStep, int)>.Empty : default;

            // for each item in the previous table, check if its still in the new items
            int itemIndex = 0;
            foreach (var (oldItem, _, _, _) in previousTable)
            {
                if (itemsSet.Remove(oldItem))
                {
                    // we're iterating the table, so know that it has entries
                    var usedCache = builder.TryUseCachedEntries(elapsedTime, noInputStepsStepInfo);
                    Debug.Assert(usedCache);
                }
                else if (inputItems.Length == previousTable.Count)
                {
                    // When the number of items matches the previous iteration, we use a heuristic to mark the input as modified
                    // This allows us to correctly 'replace' items even when they aren't actually the same. In the case that the
                    // item really isn't modified, but a new item, we still function correctly as we mostly treat them the same,
                    // but will perform an extra comparison that is omitted in the pure 'added' case.
                    var modified = builder.TryModifyEntry(inputItems[itemIndex], _comparer, elapsedTime, noInputStepsStepInfo, EntryState.Modified);
                    Debug.Assert(modified);
                    itemsSet.Remove(inputItems[itemIndex]);
                }
                else
                {
                    var removed = builder.TryRemoveEntries(elapsedTime, noInputStepsStepInfo);
                    Debug.Assert(removed);
                }
                itemIndex++;
            }

            // any remaining new items are added
            foreach (var newItem in itemsSet)
            {
                builder.AddEntry(newItem, EntryState.Added, elapsedTime, noInputStepsStepInfo, EntryState.Added);
            }

            return builder.ToImmutableAndFree();
        }

        public IIncrementalGeneratorNode<T> WithComparer(IEqualityComparer<T> comparer) => new InputNode<T>(_getInput, _registerOutput, _inputComparer, comparer, _name);

        public IIncrementalGeneratorNode<T> WithTrackingName(string name) => new InputNode<T>(_getInput, _registerOutput, _inputComparer, _comparer, name);

        public InputNode<T> WithRegisterOutput(Action<IIncrementalGeneratorOutputNode> registerOutput) => new InputNode<T>(_getInput, registerOutput, _inputComparer, _comparer, _name);

        public void RegisterOutput(IIncrementalGeneratorOutputNode output) => _registerOutput(output);
    }
}
