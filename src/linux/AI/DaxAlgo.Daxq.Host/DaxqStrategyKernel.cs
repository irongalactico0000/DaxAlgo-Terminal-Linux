using DaxAlgo.Daxq.Vm;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using TradingTerminal.Core.Backtest;
using TradingTerminal.Core.Domain;
using TradingTerminal.Core.Strategies;
using TradingTerminal.Core.Strategies.Parameters;
using TradingTerminal.Core.Time;
using TradingTerminal.Core.Trading;

namespace DaxAlgo.Daxq.Host;

/// <summary>Legacy backtest seam adapter for one verified DAXQ program.</summary>
internal sealed class DaxqStrategyKernel : IBacktestStrategy, IDisposable
{
    internal const ulong DevelopmentLaunchSeed = 0x5eedUL;
    private const int MaximumBars = 65_536;
    private const int MaximumSignals = 8;

    private readonly DaxqStrategyDefinition _definition;
    private readonly DaxqBar[] _history = new DaxqBar[MaximumBars];
    private readonly double[] _parameterValues;
    private readonly DaxqSdkAbi3FrameHost _frameHost;
    private readonly StrategySignal[] _signalBuffer = new StrategySignal[MaximumSignals];
    private DaxqLicensedProgramSession? _licensedSession;
    private DaxqProgram? _program;
    private DaxqNativeVm? _nativeVm;
    private DaxqReferenceVm? _referenceVm;
    private int _historyCount;
    private int _disposed;
    private bool _hasInitialize;
    private bool _hasOnBar;
    private bool _hasOnTick;

    public DaxqStrategyKernel(
        DaxqStrategyDefinition definition,
        Contract contract,
        StrategyParameters parameters)
    {
        _definition = definition ?? throw new ArgumentNullException(nameof(definition));
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(parameters);
        _parameterValues = definition.CreateParameterValues(parameters);
        _frameHost = new DaxqSdkAbi3FrameHost(_history, _parameterValues);
    }

    public async Task OnStartAsync(IClock clock, IOrderRouter router, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        try
        {
            _licensedSession = await _definition.ActivateAsync(ct).ConfigureAwait(false);
            _program = _licensedSession.Program;
            _hasInitialize = _program.HasEntrypoint(DaxqEntrypoint.Initialize);
            _hasOnBar = _program.HasEntrypoint(DaxqEntrypoint.OnBar);
            _hasOnTick = _program.HasEntrypoint(DaxqEntrypoint.OnTick);
            CreateRuntime();
            if (_hasInitialize)
            {
                EnsureSucceeded(InvokeInitialize(), DaxqEntrypoint.Initialize);
                await PublishSignals(router, CaptureSignals(), ct).ConfigureAwait(false);
            }
        }
        catch (DaxqStrategyRuntimeException)
        {
            CleanupFailedStart();
            throw;
        }
        catch (Exception exception)
        {
            CleanupFailedStart();
            throw RuntimeFailure("DAXQ initialization failed.", exception);
        }
    }

    public Task OnBarAsync(Bar bar, IClock clock, IOrderRouter router, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        ThrowIfLicenseInactive();

        try
        {
            AppendBar(bar);
            if (!_hasOnBar)
                return Task.CompletedTask;
            EnsureSucceeded(InvokeBar(_historyCount - 1), DaxqEntrypoint.OnBar);
            var count = CaptureSignals();
            return PublishSignals(router, count, ct);
        }
        catch (DaxqStrategyRuntimeException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw RuntimeFailure("DAXQ OnBar failed.", exception);
        }
    }

    public Task OnTickAsync(Tick tick, IClock clock, IOrderRouter router, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        ThrowIfLicenseInactive();
        if (!_hasOnTick)
            return Task.CompletedTask;

        try
        {
            var mid = (tick.Bid + tick.Ask) * 0.5d;
            var volume = (double)tick.BidSize + tick.AskSize;
            var barIndex = Math.Max(0, _frameHost.CurrentCompletedBarIndex);
            EnsureSucceeded(
                InvokeTick(barIndex, tick.Bid, tick.Ask, mid, volume),
                DaxqEntrypoint.OnTick);
            var count = CaptureSignals();
            return PublishSignals(router, count, ct);
        }
        catch (DaxqStrategyRuntimeException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw RuntimeFailure("DAXQ OnTick failed.", exception);
        }
    }

    public Task OnDepthAsync(DepthSnapshot depth, IClock clock, IOrderRouter router, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        ThrowIfLicenseInactive();
        return Task.CompletedTask;
    }

    public Task OnTradeAsync(TradePrint trade, IClock clock, IOrderRouter router, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        ThrowIfLicenseInactive();
        return Task.CompletedTask;
    }

    public Task OnOrderEventAsync(OrderEvent evt, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        ThrowIfDisposed();
        ThrowIfLicenseInactive();
        return Task.CompletedTask;
    }

    public Task OnEndAsync(IClock clock, IOrderRouter router, CancellationToken ct)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <summary>Seeds completed history for indicator lookbacks without invoking strategy code.</summary>
    public void SeedBars(IReadOnlyList<Bar> bars)
    {
        ArgumentNullException.ThrowIfNull(bars);
        ThrowIfDisposed();
        if (_historyCount != 0)
            throw RuntimeFailure("DAXQ history can only be seeded before live bars are processed.");
        if (bars.Count > MaximumBars)
            throw RuntimeFailure($"DAXQ warmup exceeds the {MaximumBars}-bar history bound.");

        try
        {
            for (var index = 0; index < bars.Count; index++)
            {
                var bar = bars[index];
                _history[index] = new DaxqBar(bar.Open, bar.High, bar.Low, bar.Close, bar.Volume);
            }
            _historyCount = bars.Count;
            _frameHost.CurrentCompletedBarIndex = _historyCount - 1;
        }
        catch (Exception exception) when (exception is not DaxqStrategyRuntimeException)
        {
            throw RuntimeFailure("DAXQ warmup history is invalid.", exception);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _licensedSession?.Dispose();
        _licensedSession = null;
        _nativeVm?.Dispose();
        _nativeVm = null;
        _referenceVm?.Dispose();
        _referenceVm = null;
        _program?.Dispose();
        _program = null;
        CryptographicOperations.ZeroMemory(MemoryMarshal.AsBytes(_parameterValues.AsSpan()));
    }

    private void CreateRuntime()
    {
        if (_nativeVm is not null || _referenceVm is not null)
            throw RuntimeFailure("The DAXQ runtime has already been initialized.");
        if (_definition.ForceReferenceVm)
        {
            _referenceVm = new DaxqReferenceVm(
                RequireProgram(), _frameHost, DevelopmentLaunchSeed);
            (_licensedSession ?? throw RuntimeFailure("The DAXQ license session is unavailable."))
                .StartReferenceVm();
            return;
        }

        if (_definition.NativeRuntimeFailure is not null)
            throw RuntimeFailure(_definition.NativeRuntimeFailure);
        var fault = DaxqNativeVm.TryCreate(
            RequireProgram(), _frameHost, DevelopmentLaunchSeed, out var native);
        if (fault != DaxqFault.Ok || native is null)
            throw RuntimeFailure($"The protected native DAXQ VM could not start: {fault}.");
        _nativeVm = native;
        var session = _licensedSession ??
            throw RuntimeFailure("The DAXQ license session is unavailable.");
        session.AttachNativeVm(native);
        session.ReleaseManagedProgram();
        _program = null;
    }

    private void CleanupFailedStart()
    {
        _licensedSession?.Dispose();
        _licensedSession = null;
        _nativeVm?.Dispose();
        _nativeVm = null;
        _referenceVm?.Dispose();
        _referenceVm = null;
        _program?.Dispose();
        _program = null;
    }

    private DaxqInvocationResult InvokeInitialize() => _nativeVm is not null
        ? _nativeVm.Initialize()
        : RequireReference().Initialize();

    private DaxqInvocationResult InvokeBar(long barIndex) => _nativeVm is not null
        ? _nativeVm.OnBar(barIndex)
        : RequireReference().OnBar(barIndex);

    private DaxqInvocationResult InvokeTick(long barIndex, double bid, double ask, double last, double volume) =>
        _nativeVm is not null
            ? _nativeVm.OnTick(barIndex, bid, ask, last, volume)
            : RequireReference().OnTick(barIndex, bid, ask, last, volume);

    private DaxqReferenceVm RequireReference() => _referenceVm ??
        throw RuntimeFailure("The DAXQ runtime has not been initialized.");

    private DaxqProgram RequireProgram() => _program ??
        throw RuntimeFailure("The DAXQ program has not been licensed and decrypted.");

    private void AppendBar(Bar bar)
    {
        if (_historyCount >= _history.Length)
            throw RuntimeFailure($"DAXQ history exceeded the {_history.Length}-bar bound.");
        _history[_historyCount++] = new DaxqBar(
            bar.Open, bar.High, bar.Low, bar.Close, bar.Volume);
        _frameHost.CurrentCompletedBarIndex = _historyCount - 1;
    }

    private int CaptureSignals()
    {
        var emitted = _nativeVm is not null
            ? _nativeVm.EmittedSignals
            : RequireReference().EmittedSignals;
        if (emitted.Length > _signalBuffer.Length)
            throw RuntimeFailure("The DAXQ VM exceeded the host signal bound.");
        for (var index = 0; index < emitted.Length; index++)
        {
            var signal = emitted[index];
            _signalBuffer[index] = new StrategySignal(
                (StrategySignalKind)signal.Kind, signal.Strength, signal.NoteId);
        }
        return emitted.Length;
    }

    private Task PublishSignals(IOrderRouter router, int count, CancellationToken ct)
    {
        if (count == 0)
            return Task.CompletedTask;
        if (router is not IStrategySignalSink sink)
            throw RuntimeFailure("The active strategy host cannot render DAXQ signals.");

        for (var index = 0; index < count; index++)
        {
            Task pending;
            try { pending = sink.EmitSignalAsync(_signalBuffer[index], ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                throw RuntimeFailure("The strategy signal sink rejected a DAXQ signal.", exception);
            }
            if (!pending.IsCompletedSuccessfully)
                return PublishRemainingAsync(sink, pending, index + 1, count, ct);
        }
        return Task.CompletedTask;
    }

    private async Task PublishRemainingAsync(
        IStrategySignalSink sink,
        Task pending,
        int next,
        int count,
        CancellationToken ct)
    {
        try
        {
            await pending.ConfigureAwait(false);
            for (var index = next; index < count; index++)
                await sink.EmitSignalAsync(_signalBuffer[index], ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw RuntimeFailure("The strategy signal sink rejected a DAXQ signal.", exception);
        }
    }

    private void EnsureSucceeded(DaxqInvocationResult result, DaxqEntrypoint entrypoint)
    {
        if (!result.Succeeded)
            throw RuntimeFailure($"DAXQ {entrypoint} faulted: {result.Fault}.");
    }

    private DaxqStrategyRuntimeException RuntimeFailure(string message, Exception? inner = null) =>
        new(_definition.PluginName, message, inner);

    private void ThrowIfDisposed()
    {
        if (Volatile.Read(ref _disposed) != 0)
            throw RuntimeFailure("The DAXQ strategy runtime has been disposed.");
    }

    private void ThrowIfLicenseInactive()
    {
        var gate = _licensedSession?.Gate;
        if (gate is null || !gate.IsAuthorized)
            throw RuntimeFailure(gate?.Reason ?? "The DAXQ strategy is not licensed for execution.");
    }
}

internal sealed class DaxqStrategyRuntimeException : InvalidOperationException, IPluginFaultAttribution
{
    public DaxqStrategyRuntimeException(string pluginName, string message, Exception? inner = null)
        : base(message, inner) => PluginName = pluginName;

    public string PluginName { get; }
}
