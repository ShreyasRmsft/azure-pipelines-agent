﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Agent.Plugins.Log.TestResultParser.Contracts;
using Agent.Sdk;
using Pipelines = Microsoft.TeamFoundation.DistributedTask.Pipelines;

namespace Agent.Plugins.Log.TestResultParser.Plugin
{
    public class TestResultLogPlugin : IAgentLogPlugin
    {
        /// <inheritdoc />
        public string FriendlyName => "TestResultLogParser";

        public TestResultLogPlugin()
        {
            // Default constructor
        }

        /// <summary>
        /// For UTs only
        /// </summary>
        public TestResultLogPlugin(ILogParserGateway inputDataParser, ITraceLogger logger, ITelemetryDataCollector telemetry)
        {
            _logger = logger;
            _telemetry = telemetry;
            _inputDataParser = inputDataParser;
        }

        /// <inheritdoc />
        public async Task<bool> InitializeAsync(IAgentLogPluginContext context)
        {
            try
            {
                _logger = _logger ?? new TraceLogger(context);
                _clientFactory = new ClientFactory(context.VssConnection);
                _telemetry = _telemetry ?? new TelemetryDataCollector(_clientFactory, _logger);

                await PopulatePipelineConfig(context);

                if (DisablePlugin(context))
                {
                    _telemetry.AddOrUpdate(TelemetryConstants.PluginDisabled, true);
                    await _telemetry.PublishCumulativeTelemetryAsync();
                    return false; // disable the plugin
                }
                
                await _inputDataParser.InitializeAsync(_clientFactory, _pipelineConfig, _logger, _telemetry);
                _telemetry.AddOrUpdate(TelemetryConstants.PluginInitialized, true);
            }
            catch (Exception ex)
            {
                context.Trace(ex.ToString());
                _logger?.Warning($"Unable to initialize {FriendlyName}.");
                if (!DisableTelemetryPublish(context) && _telemetry != null)
                {
                    _telemetry?.AddOrUpdate(TelemetryConstants.InitialzieFailed, ex);
                    await _telemetry.PublishCumulativeTelemetryAsync();
                }
                return false;
            }

            return true;
        }

        /// <inheritdoc />
        public async Task ProcessLineAsync(IAgentLogPluginContext context, Pipelines.TaskStepDefinitionReference step, string line)
        {
            await _inputDataParser.ProcessDataAsync(line);
        }

        /// <inheritdoc />
        public async Task FinalizeAsync(IAgentLogPluginContext context)
        {
            using (var timer = new SimpleTimer("Finalize", _logger,
                new TelemetryDataWrapper(_telemetry, TelemetryConstants.FinalizeAsync),
                TimeSpan.FromMilliseconds(Int32.MaxValue)))
            {
                await _inputDataParser.CompleteAsync();
            }

            if (!DisableTelemetryPublish(context))
            {
                await _telemetry.PublishCumulativeTelemetryAsync();
            }
        }

        /// <summary>
        /// Return true if plugin needs to be disabled
        /// </summary>
        private bool DisablePlugin(IAgentLogPluginContext context)
        {
            // do we want to log that the plugin is disabled due to x reason here?
            if (context.Variables.TryGetValue("Agent.ForceEnable.TestResultLogPlugin", out var forceEnableTestResultParsers)
                && string.Equals("true", forceEnableTestResultParsers.Value, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            // Enable only for build
            if (!context.Variables.TryGetValue("system.hosttype", out var hostType)
                || !string.Equals("Build", hostType.Value, StringComparison.OrdinalIgnoreCase))
            {
                _telemetry.AddOrUpdate("PluginDisabledReason", hostType?.Value);
                return true;
            }

            // Disable for on-prem
            if (!context.Variables.TryGetValue("system.servertype", out var serverType)
                || !string.Equals("Hosted", serverType.Value, StringComparison.OrdinalIgnoreCase))
            {
                _telemetry.AddOrUpdate("PluginDisabledReason", serverType?.Value);
                return true;
            }

            // check for PTR task or some other tasks to enable/disable
            if (context.Steps == null)
            {
                _telemetry.AddOrUpdate("PluginDisabledReason", "NoSteps");
                return true;
            }

            if (context.Steps.Any(x => x.Id.Equals(new Guid("0B0F01ED-7DDE-43FF-9CBB-E48954DAF9B1"))))
            {
                _telemetry.AddOrUpdate("PluginDisabledReason", "ExplicitPublishTaskPresent");
                return true;
            }

            if (_pipelineConfig.BuildId == 0)
            {
                _telemetry.AddOrUpdate("PluginDisabledReason", "BuildIdZero");
                return true;
            }

            return false;
        }

        private async Task PopulatePipelineConfig(IAgentLogPluginContext context)
        {
            var props = new Dictionary<string, Object>();

            if (context.Variables.TryGetValue("system.teamProjectId", out var projectGuid))
            {
                _pipelineConfig.Project = new Guid(projectGuid.Value);
                _telemetry.AddOrUpdate("ProjectId", _pipelineConfig.Project);
                props.Add("ProjectId", _pipelineConfig.Project);
            }

            if (context.Variables.TryGetValue("build.buildId", out var buildIdVar) && int.TryParse(buildIdVar.Value, out var buildId))
            {
                _pipelineConfig.BuildId = buildId;
                _telemetry.AddOrUpdate("BuildId", buildId);
                props.Add("BuildId", buildId);
            }

            if (context.Variables.TryGetValue("system.definitionid", out var buildDefinitionId))
            {
                _telemetry.AddOrUpdate("BuildDefinitionId", buildDefinitionId.Value);
            }

            if (context.Variables.TryGetValue("build.Repository.name", out var repositoryName))
            {
                _telemetry.AddOrUpdate("RepositoryName", repositoryName.Value);
            }

            if (context.Variables.TryGetValue("agent.version", out var agentVersion))
            {
                _telemetry.AddOrUpdate("AgentVersion", agentVersion.Value);
            }

            if (!DisableTelemetryPublish(context))
            {
                // Publish the initial telemetry event in case we are not able to fire the cumulative one for whatever reason
                await _telemetry.PublishTelemetryAsync("TestResultParserInitialize", props);
            }
        }

        /// <summary>
        /// Helper function to check whether telemetry publish needs to be disabled for cases
        /// like OnPrem where it was causing timeouts
        /// </summary>
        private bool DisableTelemetryPublish(IAgentLogPluginContext context)
        {
            if (_skipTelemetryPublish.HasValue)
            {
                return _skipTelemetryPublish.Value;
            }

            // Disable for on-prem
            if (!context.Variables.TryGetValue("system.servertype", out var serverType)
                || !string.Equals("Hosted", serverType.Value, StringComparison.OrdinalIgnoreCase))
            {
                _skipTelemetryPublish = true;
                return true;
            }

            _skipTelemetryPublish = false;
            return false;
        }

        private bool? _skipTelemetryPublish;

        private readonly ILogParserGateway _inputDataParser = new LogParserGateway();
        private IClientFactory _clientFactory;
        private ITraceLogger _logger;
        private ITelemetryDataCollector _telemetry;
        private readonly IPipelineConfig _pipelineConfig = new PipelineConfig();
    }
}