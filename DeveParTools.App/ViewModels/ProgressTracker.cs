using DeveParTools.Core.Operations;

namespace DeveParTools.App.ViewModels;

public enum StageStatus
{
    Pending,
    Active,
    Done,
    Skipped,
}

/// <summary>One step as last read from a <see cref="ProgressTracker"/>.</summary>
public sealed record StageSnapshot(string Name, string Description, double Weight, double Fraction, StageStatus Status);

/// <summary>
/// The steps of the running operation and how far each one is, fed from the worker threads (plan, stage starts,
/// progress) and read by the UI on a timer. Thread-safe.
/// </summary>
/// <remarks>
/// <para>The total is the weighted average of the steps: Σ weight × fraction ÷ Σ weight, where finished steps count as
/// 1 and steps that turned out not to be needed are left out. Example: Hashing (weight 10, done) and Encoding (weight
/// 30, at 50%) give (10 + 15) ÷ 40 = 62.5%.</para>
/// <para>A new plan keeps the progress of the steps it shares with the old one (operations refine their plan once they
/// know the file sizes). A step that starts without being in the plan is added after the current one, with the same
/// weight.</para>
/// </remarks>
public sealed class ProgressTracker
{
    private readonly Lock _lock = new();
    private readonly List<Item> _items = [];
    private int _active = -1;

    private sealed class Item(string name, string description, double weight)
    {
        public string Name { get; } = name;

        public string Description { get; set; } = description;

        public double Weight { get; set; } = weight;

        public double Fraction { get; set; }

        public StageStatus Status { get; set; }
    }

    public void Reset()
    {
        lock (_lock)
        {
            _items.Clear();
            _active = -1;
        }
    }

    public void Plan(IReadOnlyList<OperationStage> stages)
    {
        lock (_lock)
        {
            var old = _items.ToDictionary(i => i.Name);
            string? activeName = _active >= 0 ? _items[_active].Name : null;
            _items.Clear();
            foreach (var s in stages)
            {
                if (old.Remove(s.Name, out var kept))
                {
                    kept.Description = s.Description;
                    kept.Weight = s.Weight;
                    _items.Add(kept);
                }
                else
                {
                    _items.Add(new Item(s.Name, s.Description, s.Weight));
                }
            }

            _active = activeName == null ? -1 : _items.FindIndex(i => i.Name == activeName);
        }
    }

    public void Stage(string name)
    {
        lock (_lock)
        {
            if (_active >= 0)
            {
                _items[_active].Fraction = 1;
                _items[_active].Status = StageStatus.Done;
            }

            int index = _items.FindIndex(Math.Max(0, _active), i => i.Name == name);
            if (index < 0)
            {
                // Not announced (e.g. a second attempt): add it after the current step.
                index = _active + 1;
                _items.Insert(index, new Item(name, "A further step of the operation.", _active >= 0 ? _items[_active].Weight : 1));
            }

            // Steps between the previous one and this one did not run: they were not needed.
            for (int i = Math.Max(0, _active + 1); i < index; i++)
            {
                if (_items[i].Status == StageStatus.Pending)
                {
                    _items[i].Status = StageStatus.Skipped;
                }
            }

            _active = index;
            _items[index].Status = StageStatus.Active;
            _items[index].Fraction = 0;
        }
    }

    public void Progress(double fraction)
    {
        lock (_lock)
        {
            if (_active >= 0)
            {
                _items[_active].Fraction = Math.Clamp(fraction, 0, 1);
            }
        }
    }

    /// <summary>The operation ended: the last step is done when it succeeded, and steps that never ran were not needed.</summary>
    public void Finish(bool success)
    {
        lock (_lock)
        {
            if (success && _active >= 0)
            {
                _items[_active].Fraction = 1;
                _items[_active].Status = StageStatus.Done;
            }

            foreach (var i in _items.Where(i => i.Status == StageStatus.Pending && success))
            {
                i.Status = StageStatus.Skipped;
            }
        }
    }

    public (IReadOnlyList<StageSnapshot> Stages, double Total) Read()
    {
        lock (_lock)
        {
            var stages = _items.Select(i => new StageSnapshot(i.Name, i.Description, i.Weight, i.Fraction, i.Status)).ToList();
            var counted = _items.Where(i => i.Status != StageStatus.Skipped).ToList();
            double weight = counted.Sum(i => i.Weight);
            double total = weight <= 0 ? (counted.Count > 0 && counted.All(i => i.Status == StageStatus.Done) ? 1 : 0) : counted.Sum(i => i.Weight * (i.Status == StageStatus.Done ? 1 : i.Fraction)) / weight;
            return (stages, total);
        }
    }
}
