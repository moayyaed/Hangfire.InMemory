﻿using System;
using System.Collections.Generic;
using Hangfire.Annotations;
using Hangfire.States;
using Hangfire.Storage;

namespace Hangfire.InMemory
{
    internal sealed class InMemoryTransaction : JobStorageTransaction
    {
        private readonly List<Action<InMemoryState>> _actions = new List<Action<InMemoryState>>();
        private readonly HashSet<QueueEntry> _enqueued = new HashSet<QueueEntry>();
        private readonly InMemoryDispatcherBase _dispatcher;

        public InMemoryTransaction(InMemoryDispatcherBase dispatcher)
        {
            _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        }

        public override void ExpireJob([NotNull] string jobId, TimeSpan expireIn)
        {
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));

            _actions.Add(state =>
            {
                if (state.Jobs.TryGetValue(jobId, out var job))
                {
                    state.JobExpire(job, expireIn);
                }
            });
        }

        public override void PersistJob([NotNull] string jobId)
        {
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));

            _actions.Add(state =>
            {
                if (state.Jobs.TryGetValue(jobId, out var job))
                {
                    state.JobExpire(job, null);
                }
            });
        }

        public override void SetJobState([NotNull] string jobId, [NotNull] IState state)
        {
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (state.Name == null) throw new ArgumentException("Name property must not return null.", nameof(state));

            var stateEntry = new StateEntry
            {
                Name = state.Name,
                Reason = state.Reason,
                Data = state.SerializeData()
            };

            _actions.Add(memory =>
            {
                if (memory.Jobs.TryGetValue(jobId, out var job))
                {
                    stateEntry.CreatedAt = memory.TimeResolver();

                    job.History.Add(stateEntry);
                    memory.JobSetState(job, stateEntry);
                }
            });
        }

        public override void AddJobState([NotNull] string jobId, [NotNull] IState state)
        {
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));
            if (state == null) throw new ArgumentNullException(nameof(state));

            var stateEntry = new StateEntry
            {
                Name = state.Name,
                Reason = state.Reason,
                Data = state.SerializeData()
            };

            _actions.Add(memory =>
            {
                if (memory.Jobs.TryGetValue(jobId, out var job))
                {
                    stateEntry.CreatedAt = memory.TimeResolver();
                    job.History.Add(stateEntry);
                }
            });
        }

        public override void AddToQueue([NotNull] string queue, [NotNull] string jobId)
        {
            if (queue == null) throw new ArgumentNullException(nameof(queue));
            if (jobId == null) throw new ArgumentNullException(nameof(jobId));

            _actions.Add(state =>
            {
                var entry = state.QueueGetOrCreate(queue);
                entry.Queue.Enqueue(jobId);

                _enqueued.Add(entry);
            });
        }

        public override void IncrementCounter([NotNull] string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            _actions.Add(state => CounterIncrement(state, key, 1, null));
        }

        public override void IncrementCounter([NotNull] string key, TimeSpan expireIn)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            _actions.Add(state => CounterIncrement(state, key, 1, expireIn));
        }

        public override void DecrementCounter([NotNull] string key)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            _actions.Add(state => CounterIncrement(state, key, -1, null));
        }

        public override void DecrementCounter([NotNull] string key, TimeSpan expireIn)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            _actions.Add(state => CounterIncrement(state, key, -1, expireIn));
        }

        public override void AddToSet([NotNull] string key, [NotNull] string value)
        {
            AddToSet(key, value, 0.0D);
        }

        public override void AddToSet([NotNull] string key, [NotNull] string value, double score)
        {
            if (key == null) throw new ArgumentNullException(nameof(key));
            if (value == null) throw new ArgumentNullException(nameof(value));

            _actions.Add(state => { state.SetGetOrAdd(key).Add(value, score); });
        }

        public override void RemoveFromSet(string key, string value)
        {
            _actions.Add(state =>
            {
                if (!state.Sets.TryGetValue(key, out var set)) return;

                set.Remove(value);

                if (set.Count == 0) state.SetDelete(set);
            });
        }

        public override void InsertToList(string key, string value)
        {
            _actions.Add(state =>
            {
                // TODO: Possible that value is null?
                var list = state.ListGetOrAdd(key);
                list.Add(value);
            });
        }

        public override void RemoveFromList(string key, string value)
        {
            _actions.Add(state =>
            {
                // TODO: Possible that value is null?
                var list = state.ListGetOrAdd(key);
                // TODO: Does this remove all occurrences?
                list.Remove(value);

                if (list.Count == 0) state.ListDelete(list);
            });
        }

        public override void TrimList(string key, int keepStartingFrom, int keepEndingAt)
        {
            _actions.Add(state =>
            {
                if (!state.Lists.TryGetValue(key, out var list)) return;

                var result = new List<string>(); // TODO: Create only when really necessary

                for (var index = 0; index < list.Count; index++)
                {
                    if (index >= keepStartingFrom && index <= keepEndingAt)
                    {
                        // TODO: Ensure resulting list is not inverted
                        result.Add(list[index]);
                    }
                }

                if (result.Count > 0)
                {
                    // TODO: Replace with better version
                    list.Update(result);
                }
                else
                {
                    state.ListDelete(list);
                }
            });
        }

        public override void SetRangeInHash(string key, IEnumerable<KeyValuePair<string, string>> keyValuePairs)
        {
            _actions.Add(state =>
            {
                // TODO: Avoid creating a hash when values are empty
                var hash = state.HashGetOrAdd(key);

                foreach (var valuePair in keyValuePairs)
                {
                    hash.Value[valuePair.Key] = valuePair.Value;
                }

                if (hash.Value.Count == 0)
                {
                    state.HashDelete(hash);
                }
            });
        }

        public override void RemoveHash(string key)
        {
            _actions.Add(state =>
            {
                if (state.Hashes.TryGetValue(key, out var hash)) state.HashDelete(hash);
            });
        }

        public override void AddRangeToSet(string key, IList<string> items)
        {
            _actions.Add(state =>
            {
                // TODO: Don't do anything when items is empty
                var set = state.SetGetOrAdd(key);

                // TODO: Original implementation in SQL Server expects all the items in this case don't exist.
                foreach (var value in items)
                {
                    set.Add(value, 0.0D);
                }
            });
        }

        public override void RemoveSet(string key)
        {
            _actions.Add(state =>
            {
                if (state.Sets.TryGetValue(key, out var set)) state.SetDelete(set);
            });
        }

        public override void ExpireHash(string key, TimeSpan expireIn)
        {
            _actions.Add(state =>
            {
                if (state.Hashes.TryGetValue(key, out var hash)) state.HashExpire(hash, expireIn);
            });
        }

        public override void ExpireList(string key, TimeSpan expireIn)
        {
            _actions.Add(state =>
            {
                if (state.Lists.TryGetValue(key, out var list)) state.ListExpire(list, expireIn);
            });
        }

        public override void ExpireSet(string key, TimeSpan expireIn)
        {
            _actions.Add(state =>
            {
                if (state.Sets.TryGetValue(key, out var set)) state.SetExpire(set, expireIn);
            });
        }

        public override void PersistHash(string key)
        {
            _actions.Add(state =>
            {
                if (state.Hashes.TryGetValue(key, out var hash)) state.HashExpire(hash, null);
            });
        }

        public override void PersistList(string key)
        {
            _actions.Add(state =>
            {
                if (state.Lists.TryGetValue(key, out var list)) state.ListExpire(list, null);
            });
        }

        public override void PersistSet(string key)
        {
            _actions.Add(state =>
            {
                if (state.Sets.TryGetValue(key, out var set)) state.SetExpire(set, null);
            });
        }

        public override void Commit()
        {
            _dispatcher.QueryAndWait(state =>
            {
                // TODO: Check all the preconditions (for example if locks are still held)
                foreach (var action in _actions)
                {
                    action(state);
                }
            });

            foreach (var queue in _enqueued)
            {
                _dispatcher.SignalOneQueueWaitNode(queue);
            }
        }

        private static void CounterIncrement(InMemoryState state, string key, int value, TimeSpan? expireIn)
        {
            var counter = state.CounterGetOrAdd(key);
            counter.Value += value;

            if (counter.Value != 0)
            {
                if (expireIn.HasValue)
                {
                    state.CounterExpire(counter, expireIn);
                }
            }
            else
            {
                state.CounterDelete(counter);
            }
        }
    }
}