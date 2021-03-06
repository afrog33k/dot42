﻿using System;
using System.Diagnostics;
using System.Linq;
using Dot42.DexLib;

namespace Dot42.CompilerLib.RL.Transformations
{
    /// <summary>
    /// Flatten exception handlers
    /// </summary>
    internal sealed class FlattenExceptionsTransformation : IRLTransformation
    {
        /// <summary>
        /// Transform the given body.
        /// </summary>
        public bool Transform(Dex target, MethodBody body)
        {
            var handlers = body.Exceptions;
            if (handlers.Count <= 1)
                return false;

            // Create a list used for sorting and overlapping. outer handlers come before inner handlers
            var fhandlers = handlers.Select(x => new FlattenableExceptionHandler(x)).ToList();
            fhandlers.Sort();

            // Rebuild handler list
            for (var i = 0; i < fhandlers.Count - 1; )
            {
                var outer = fhandlers[i];
                var inner = fhandlers[i + 1];
                if (!outer.Overlaps(inner))
                {
                    i++;
                    continue;
                }

                // 'outer' and 'inner' overlap.
                if (outer.TryStart < inner.TryStart)
                {
                    // Create handler for start of 'outer' only.
                    var prefix = new ExceptionHandler(outer.Handler);
                    prefix.TryEnd = inner.Handler.TryStart.Previous;
                    Debug.Assert(prefix.TryEnd != null);
                    fhandlers.Insert(i, new FlattenableExceptionHandler(prefix));

                    // Trim original 'outer' handler
                    outer.Handler.TryStart = inner.Handler.TryStart;
                }

                // start of 'outer' and 'inner' must now be equal
                Debug.Assert(outer.TryStart == inner.TryStart);

                // Create handler for shared block, based on the inner handler.
                var common = new ExceptionHandler(inner.Handler);

                // add the outer handlers, but only if they do not mask a catch-all.
                if (inner.Handler.CatchAll == null)
                {
                    common.Catches.AddRange(outer.Handler.Catches.Where(x => !inner.Handler.Catches.Any(y => x.Type.Equals(y.Type))));
                    common.CatchAll = outer.Handler.CatchAll;

                }
                fhandlers.Add(new FlattenableExceptionHandler(common));

                // Now remove 'inner'
                fhandlers.Remove(inner);

                // If 'outer' ends at same instruction as 'inner', remove 'outer' also.
                if (outer.TryEnd == inner.TryEnd)
                {
                    fhandlers.Remove(outer);
                }
                else
                {
                    // Update 'outer', so it starts after 'inner'
                    outer.Handler.TryStart = inner.Handler.TryEnd.Next;
                    Debug.Assert(outer.Handler.TryStart != null);
                }

                // Sort and restart
                fhandlers.Sort();
                i = 0;
            }

            // Now there should not be any overlapping handlers left.
            // Rebuild the handler list and check non-overlapping condition
            handlers.Clear();
            FlattenableExceptionHandler prev = null;
            foreach (var handler in fhandlers)
            {
                if ((prev != null) && prev.Overlaps(handler))
                {
                    throw new InvalidProgramException("Handler must not overlap");
                }
                handlers.Add(handler.Handler);
                prev = handler;
            }

            return false;
        }

        /// <summary>
        /// Helper class for flattening exception handlers
        /// </summary>
        [DebuggerDisplay("{TryStart}-{TryEnd}")]
        private sealed class FlattenableExceptionHandler : IComparable<FlattenableExceptionHandler>
        {
            private readonly ExceptionHandler handler;

            /// <summary>
            /// Default ctor
            /// </summary>
            public FlattenableExceptionHandler(ExceptionHandler handler)
            {
                this.handler = handler;
            }

            /// <summary>
            /// Gets the index of the first instruction in the try block.
            /// </summary>
            public int TryStart { get { return handler.TryStart.Index; } }

            /// <summary>
            /// Gets the index of the last instruction in the try block.
            /// </summary>
            public int TryEnd { get { return handler.TryEnd.Index; } }

            /// <summary>
            /// Gets the original handler
            /// </summary>
            public ExceptionHandler Handler { get { return handler; } }

            /// <summary>
            /// Compares the current object with another object of the same type.
            /// The result is such that surrounding handlers are found before their nested handlers.
            /// </summary>
            public int CompareTo(FlattenableExceptionHandler other)
            {
                if (TryStart < other.TryStart) return -1;
                if (TryStart > other.TryStart) return 1;

                // Start equal
                if (TryEnd > other.TryEnd) return -1;
                if (TryEnd < other.TryEnd) return 1;

                return 0;
            }

            /// <summary>
            /// Does the try block of this handler overlap the try block of the other handler?
            /// </summary>
            public bool Overlaps(FlattenableExceptionHandler other)
            {
                if (TryEnd < other.TryStart) return false;
                if (TryStart > other.TryEnd) return false;
                return true;
            }
        }
    }
}
