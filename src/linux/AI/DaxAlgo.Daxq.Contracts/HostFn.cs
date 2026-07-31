namespace DaxAlgo.Daxq.Contracts;

/// <summary>Canonical host callback IDs for VM ABI 3. Numeric assignments are immutable.</summary>
public enum HostFn : ushort
{
    /// <summary>Read one OHLCV field at a bounded lookback.</summary>
    Bar = 1,

    /// <summary>Read one deterministic SDK indicator value.</summary>
    Ind = 2,

    /// <summary>Read one numeric strategy parameter.</summary>
    Param = 3,

    /// <summary>Stage one strategy signal.</summary>
    Emit = 4,

    /// <summary>
    /// Identifies the VM-mediated state facility. Bytecode uses LD_STATE/ST_STATE; CALL_HOST State is
    /// invalid.
    /// </summary>
    State = 5,

    /// <summary>Read the current deterministic bar index.</summary>
    TIndex = 6,

    /// <summary>Read the next seeded deterministic pseudo-random value.</summary>
    Rng = 7,

    /// <summary>Stage one bounded numeric diagnostic record.</summary>
    Log = 8,
}
