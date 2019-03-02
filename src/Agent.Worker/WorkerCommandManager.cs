﻿using Microsoft.TeamFoundation.DistributedTask.WebApi;
using Microsoft.VisualStudio.Services.Agent.Util;
using System;
using System.Collections.Generic;

namespace Microsoft.VisualStudio.Services.Agent.Worker
{
    [ServiceLocator(Default = typeof(WorkerCommandManager))]
    public interface IWorkerCommandManager : IAgentService
    {
        bool TryProcessCommand(IExecutionContext context, string input);
        void EnableCommandExtension(IExecutionContext context, string commandArea);
        void DisableCommandExtension(IExecutionContext context, string commandArea);
    }

    public sealed class WorkerCommandManager : AgentService, IWorkerCommandManager
    {
        private readonly Dictionary<string, IWorkerCommandExtension> _commandExtensions = new Dictionary<string, IWorkerCommandExtension>(StringComparer.OrdinalIgnoreCase);
        private readonly object _commandSerializeLock = new object();

        public override void Initialize(IHostContext hostContext)
        {
            base.Initialize(hostContext);

            // Register all command extensions
            var extensionManager = hostContext.GetService<IExtensionManager>();
            foreach (var commandExt in extensionManager.GetExtensions<IWorkerCommandExtension>() ?? new List<IWorkerCommandExtension>())
            {
                Trace.Info($"Register command extension for area {commandExt.CommandArea}");
                _commandExtensions[commandExt.CommandArea] = commandExt;
            }
        }

        public void EnableCommandExtension(IExecutionContext context, string commandArea)
        {
            if (_commandExtensions.TryGetValue(commandArea, out IWorkerCommandExtension extension))
            {
                Trace.Info($"Enable command extension '{commandArea}'");
                extension.Enabled = true;
            }
            else
            {
                throw new ArgumentException(StringUtil.Loc("CommandNotFound", commandArea));
            }
        }

        public void DisableCommandExtension(IExecutionContext context, string commandArea)
        {
            if (_commandExtensions.TryGetValue(commandArea, out IWorkerCommandExtension extension))
            {
                Trace.Info($"Disable command extension '{commandArea}'");
                extension.Enabled = false;
            }
            else
            {
                throw new ArgumentException(StringUtil.Loc("CommandNotFound", commandArea));
            }
        }

        public bool TryProcessCommand(IExecutionContext context, string input)
        {
            if (string.IsNullOrEmpty(input))
            {
                return false;
            }

            // TryParse input to Command
            Command command;
            if (!Command.TryParse(input, out command))
            {
                // if parse fail but input contains ##vso, print warning with DOC link
                if (input.IndexOf("##vso") >= 0)
                {
                    context.Warning(StringUtil.Loc("CommandKeywordDetected", input));
                }

                return false;
            }

            if (_commandExtensions.TryGetValue(command.Area, out IWorkerCommandExtension extension))
            {
                if (!extension.Enabled)
                {
                    context.Error($"Command '{extension.CommandArea}' is not enabled for the current context.");
                    context.CommandResult = TaskResult.Failed;
                    return false;
                }

                if (!extension.SupportedHostTypes.HasFlag(context.Variables.System_HostType))
                {
                    context.Error(StringUtil.Loc("CommandNotSupported", command.Area, context.Variables.System_HostType));
                    context.CommandResult = TaskResult.Failed;
                    return false;
                }

                // process logging command in serialize oreder.
                lock (_commandSerializeLock)
                {
                    try
                    {
                        extension.ProcessCommand(context, command);
                    }
                    catch (Exception ex)
                    {
                        context.Error(StringUtil.Loc("CommandProcessFailed", input));
                        context.Error(ex);
                        context.CommandResult = TaskResult.Failed;
                    }
                    finally
                    {
                        // trace the ##vso command as long as the command is not a ##vso[task.debug] command.
                        if (!(string.Equals(command.Area, "task", StringComparison.OrdinalIgnoreCase) &&
                              string.Equals(command.Event, "debug", StringComparison.OrdinalIgnoreCase)))
                        {
                            context.Debug($"Processed: {input}");
                        }
                    }
                }
            }
            else
            {
                context.Warning(StringUtil.Loc("CommandNotFound", command.Area));
            }

            return true;
        }
    }

    public interface IWorkerCommandExtension : IExtension
    {
        string CommandArea { get; }

        bool Enabled { get; set; }

        HostTypes SupportedHostTypes { get; }

        void ProcessCommand(IExecutionContext context, Command command);
    }

    [Flags]
    public enum HostTypes
    {
        None = 0,
        Build = 1,
        Deployment = 2,
        PoolMaintenance = 4,
        Release = 8,
        All = Build | Deployment | PoolMaintenance | Release,
    }
}
