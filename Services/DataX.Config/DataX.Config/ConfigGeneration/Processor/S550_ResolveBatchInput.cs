﻿// *********************************************************************
// Copyright (c) Microsoft Corporation.  All rights reserved.
// Licensed under the MIT License
// *********************************************************************
using DataX.Config.ConfigDataModel;
using DataX.Config.ConfigDataModel.RuntimeConfig;
using DataX.Config.Utility;
using DataX.Contract;
using System;
using System.Composition;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DataX.Config.ConfigGeneration.Processor
{
    /// <summary>
    /// Produce the time window section
    /// </summary>
    [Shared]
    [Export(typeof(IFlowDeploymentProcessor))]
    public class ResolveBatchInput : ProcessorBase
    {
        public const string TokenName_InputBatching = "inputBatching";

        [ImportingConstructor]
        public ResolveBatchInput(ConfigGenConfiguration configuration, IKeyVaultClient keyVaultClient)
        {
            Configuration = configuration;
            KeyVaultClient = keyVaultClient;
        }

        private ConfigGenConfiguration Configuration { get; }
        private IKeyVaultClient KeyVaultClient { get; }

        public override async Task<FlowGuiConfig> HandleSensitiveData(FlowGuiConfig guiConfig)
        {
            if (guiConfig?.Input?.Mode == Constants.InputMode_Batching)
            {
                var runtimeKeyVaultName = Configuration[Constants.ConfigSettingName_RuntimeKeyVaultName];
                Ensure.NotNull(runtimeKeyVaultName, "runtimeKeyVaultName");

                for (int i = 0; i < guiConfig?.Input?.Batch?.Length; i++)
                {
                    // Replace Input Path
                    var input = guiConfig?.Input?.Batch[i];
                    var inputConnection = input.Properties.Connection;
                    if (!string.IsNullOrEmpty(inputConnection) && !KeyVaultUri.IsSecretUri(inputConnection))
                    {
                        var secretName = $"{guiConfig.Name}-input-{i}-inputConnection";
                        var secretId = await KeyVaultClient.SaveSecretAsync(runtimeKeyVaultName, secretName, inputConnection).ConfigureAwait(false);
                        input.Properties.Connection = secretId;
                    }

                    var inputPath = input.Properties.Path;
                    if (!string.IsNullOrEmpty(inputPath) && !KeyVaultUri.IsSecretUri(inputPath))
                    {
                        var secretName = $"{guiConfig.Name}-input-{i}-inputPath";
                        var secretId = await KeyVaultClient.SaveSecretAsync(runtimeKeyVaultName, secretName, inputPath).ConfigureAwait(false);
                        input.Properties.Path = secretId;
                    }
                }
            }

            return guiConfig;
        }

        public override async Task<string> Process(FlowDeploymentSession flowToDeploy)
        {
            if (flowToDeploy.Config.GetGuiConfig().Input?.Mode == Constants.InputMode_Batching)
            {
                var inputConfig = flowToDeploy.Config.GetGuiConfig();

                var inputBatching = inputConfig.Input.Batch ?? Array.Empty<FlowGuiInputBatchInput>();
                var specsTasks = inputBatching.Select(async rd =>
                {
                    var connectionString = await KeyVaultClient.ResolveSecretUriAsync(rd.Properties.Connection).ConfigureAwait(false);
                    var inputPath = await KeyVaultClient.ResolveSecretUriAsync(rd.Properties.Path).ConfigureAwait(false);

                    return new InputBatchingSpec()
                    {
                        Name = ParseBlobAccountName(connectionString),
                        Path = rd.Properties.Path,
                        Format = rd.Properties.FormatType,
                        CompressionType = rd.Properties.CompressionType,
                        ProcessStartTime = "",
                        ProcessEndTime = "",
                        PartitionIncrement = GetPartitionIncrement(inputPath).ToString(CultureInfo.InvariantCulture),
                    };
                }).ToArray();

                var specs = await Task.WhenAll(specsTasks).ConfigureAwait(false);

                flowToDeploy.SetAttachment(TokenName_InputBatching, specs);
            }

            return "done";
        }

        private static long GetPartitionIncrement(string path)
        {
            Regex regex = new Regex(@"\{([yMdHhmsS\-\/.,: ]+)\}*", RegexOptions.IgnoreCase);
            Match mc = regex.Match(path);

            if (mc != null && mc.Success && mc.Groups.Count > 1)
            {
                var value = mc.Groups[1].Value.Trim();

                value = value.Replace(@"[\/:\s-]", "", StringComparison.InvariantCultureIgnoreCase).Replace(@"(.)(?=.*\1)", "", StringComparison.InvariantCultureIgnoreCase);

                if (value.Contains("h", StringComparison.InvariantCultureIgnoreCase))
                {
                    return 1 * 60;
                }
                else if (value.Contains("d", StringComparison.InvariantCultureIgnoreCase))
                {
                    return 1 * 60 * 24;
                }
                else if (value.Contains("M", StringComparison.InvariantCulture))
                {
                    return 1 * 60 * 24 * 30;
                }
                else if (value.Contains("y", StringComparison.InvariantCultureIgnoreCase))
                {
                    return 1 * 60 * 24 * 30 * 12;
                }
            }

            return 1;
        }

        private static string ParseBlobAccountName(string connectionString)
        {
            string matched;
            try
            {
                matched = Regex.Match(connectionString, @"(?<=AccountName=)(.*)(?=;AccountKey)").Value;
            }
            catch (Exception)
            {
                return "The connectionString does not have AccountName";
            }

            return matched;
        }

    }
}