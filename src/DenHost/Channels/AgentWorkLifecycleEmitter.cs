using System.Text.Json;
using DenHost.Clients;
using DenHost.Configuration;
using DenHost.Host;
using DenHost.Worker;
using Microsoft.Extensions.Logging;

namespace DenHost.Channels;

/// <summary>
/// Emits machine-owned agent-work lifecycle observability to Den Channels.
/// This is evidence only: Den Core remains authoritative for assignments/runs,
/// and Channels lifecycle events are non-waking activity records.
/// </summary>
public interface IAgentWorkLifecycleEmitter
{
    Task EmitDirectAgentRuntimeReceivedAsync(
        ChannelsEvent evt,
        EventMatchOutcome outcome,
        CancellationToken cancellationToken);

    Task EmitRunLifecycleAsync(
        LocalRunRecord record,
        string eventType,
        string stateReason,
        CancellationToken cancellationToken,
        string? summary = null,
        string? dedupeSuffix = null);
}

public sealed class AgentWorkLifecycleEmitter : IAgentWorkLifecycleEmitter
{
    private static readonly JsonSerializerOptions MetadataJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IChannelsClient _channels;
    private readonly ChannelsOptions _channelsOptions;
    private readonly AdapterIdentity _identity;
    private readonly ILogger<AgentWorkLifecycleEmitter> _logger;

    public AgentWorkLifecycleEmitter(
        IChannelsClient channels,
        ChannelsOptions channelsOptions,
        AdapterIdentity identity,
        ILogger<AgentWorkLifecycleEmitter> logger)
    {
        _channels = channels;
        _channelsOptions = channelsOptions;
        _identity = identity;
        _logger = logger;
    }

    public Task EmitDirectAgentRuntimeReceivedAsync(
        ChannelsEvent evt,
        EventMatchOutcome outcome,
        CancellationToken cancellationToken)
    {
        var memberIdentity = ExtractDirectAgentMemberIdentity(evt.SourceId)
            ?? evt.PoolMemberId
            ?? evt.ProfileIdentity
            ?? evt.SenderIdentity;
        var metadata = new
        {
            source = "den-host",
            producer = "channels_event_reader",
            outcome.IsForUs,
            outcome.Reason,
            outcome.IntendedAction,
            outcome.MigrationDiffNote,
            evt.MessageKind,
            evt.SourceKind,
            evt.SourceId,
            evt.DedupeKey,
            evt.DeepLink,
        };
        var request = new AgentWorkLifecycleWriteRequest
        {
            ChannelId = evt.ChannelId,
            AgentIdentity = memberIdentity,
            EventType = "runtime_received",
            ProjectId = evt.TargetProjectId ?? evt.SourceProjectId,
            TaskId = evt.TargetTaskId,
            ProfileIdentity = evt.ProfileIdentity,
            AgentInstanceId = evt.AgentInstanceId,
            WorkerIdentity = evt.PoolMemberId,
            WorkerRole = evt.WorkerRole,
            PoolMemberId = evt.PoolMemberId,
            AssignmentId = evt.AssignmentId,
            WorkerRunId = evt.WorkerRunId,
            SessionId = evt.SessionId,
            DeliveryRequestId = evt.DeliveryRequestId,
            SourceMessageId = evt.SourceId,
            DirectAgentEventId = evt.EventId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            HostId = _identity.InstanceId,
            LastActivityAt = DateTimeOffset.UtcNow.ToString("O"),
            StateReason = outcome.IsForUs ? outcome.Reason : $"not_for_us:{outcome.Reason}",
            Title = outcome.IsForUs ? "Runtime received matching direct-agent event" : "Runtime observed direct-agent event",
            Summary = evt.Summary,
            MetadataJson = JsonSerializer.Serialize(metadata, MetadataJsonOptions),
            DedupeKey = $"den-host:{_identity.InstanceId}:runtime_received:{evt.EventId}",
        };
        return EmitAsync(request, cancellationToken);
    }

    public Task EmitRunLifecycleAsync(
        LocalRunRecord record,
        string eventType,
        string stateReason,
        CancellationToken cancellationToken,
        string? summary = null,
        string? dedupeSuffix = null)
    {
        if (_channelsOptions.EventsListChannelId is not long channelId)
        {
            _logger.LogDebug(
                "Skipping run lifecycle event {EventType} for {LocalRunId}; Channels:EventsListChannelId is not configured.",
                eventType, record.LocalRunId);
            return Task.CompletedTask;
        }

        var metadata = new
        {
            source = "den-host",
            producer = "run_reconciliation",
            record.LocalRunId,
            record.HarnessKind,
            record.HarnessModuleName,
            record.RunDir,
            record.LogFilePath,
            record.StartedAt,
            record.State,
        };
        var request = new AgentWorkLifecycleWriteRequest
        {
            ChannelId = channelId,
            AgentIdentity = record.PoolMemberId ?? record.ProfileIdentity ?? _identity.InstanceId,
            EventType = eventType,
            TaskId = record.TaskId,
            ProfileIdentity = record.ProfileIdentity,
            WorkerIdentity = record.PoolMemberId,
            WorkerRole = record.Role,
            PoolMemberId = record.PoolMemberId,
            AssignmentId = record.AssignmentId?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            WorkerRunId = record.WorkerRunId,
            HostId = _identity.InstanceId,
            ProcessId = record.ProcessId,
            Workdir = record.RunDir,
            LastActivityAt = DateTimeOffset.UtcNow.ToString("O"),
            StateReason = stateReason,
            Title = $"den-host {eventType.Replace('_', ' ')}",
            Summary = summary ?? stateReason,
            MetadataJson = JsonSerializer.Serialize(metadata, MetadataJsonOptions),
            DedupeKey = $"den-host:{_identity.InstanceId}:{eventType}:{record.LocalRunId}:{dedupeSuffix ?? record.State.ToString()}",
        };
        return EmitAsync(request, cancellationToken);
    }

    private async Task EmitAsync(AgentWorkLifecycleWriteRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _channels.PostAgentWorkLifecycleEventAsync(request, cancellationToken).ConfigureAwait(false);
            if (!result.Ok)
            {
                _logger.LogDebug(
                    "Lifecycle event {EventType} for {AgentIdentity} was not recorded: {StatusCode} {Diagnostic}",
                    request.EventType, request.AgentIdentity, result.StatusCode, result.Diagnostic);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "Lifecycle event {EventType} for {AgentIdentity} failed; den-host will continue.",
                request.EventType, request.AgentIdentity);
        }
    }

    private static string? ExtractDirectAgentMemberIdentity(string? sourceId)
    {
        if (string.IsNullOrWhiteSpace(sourceId)) return null;
        var parts = sourceId.Split(':');
        if (parts.Length < 4 || !string.Equals(parts[0], "direct-agent-message", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        try
        {
            return Uri.UnescapeDataString(parts[2]);
        }
        catch
        {
            return parts[2];
        }
    }
}
