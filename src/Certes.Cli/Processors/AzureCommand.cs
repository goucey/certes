﻿using System;
using System.CommandLine;
using System.Threading.Tasks;
using Certes.Cli.Options;
using Microsoft.Azure.Management.AppService.Fluent;
using Microsoft.Azure.Management.Dns.Fluent;
using Microsoft.Azure.Management.Dns.Fluent.Models;
using Microsoft.Azure.Management.ResourceManager.Fluent;
using Microsoft.Azure.Management.ResourceManager.Fluent.Authentication;
using Newtonsoft.Json;
using NLog;

namespace Certes.Cli.Processors
{
    internal class AzureCommand
    {
        public ILogger Logger { get; } = LogManager.GetCurrentClassLogger();
        private AzureOptions Args { get; }

        public AzureCommand(AzureOptions args)
        {
            Args = args;
        }

        public static AzureOptions TryParse(ArgumentSyntax syntax)
        {
            var options = new AzureOptions();

            var command = Command.Undefined;
            syntax.DefineCommand("azure", ref command, Command.Azure, "Deploy to Azure.");
            if (command == Command.Undefined)
            {
                return null;
            }

            syntax.DefineOption("user", ref options.UserName, $"Azure user name or client ID.");
            syntax.DefineOption("pwd", ref options.Password, $"Azure password or client secret.");
            syntax.DefineOption<Guid>("talent", ref options.Talent, $"Azure talent ID.");
            syntax.DefineOption<Guid>("subscription", ref options.Subscription, $"Azure subscription ID.");
            syntax.DefineOption<Uri>("order", ref options.OrderUri, $"ACME order URI.");
            syntax.DefineEnumOption("cloud", ref options.CloudEnvironment, $"ACME order URI.");
            syntax.DefineOption("resourceGroup", ref options.ResourceGroup, $"The resource group.");

            syntax.DefineOption<Uri>("server", ref options.Server, $"ACME Directory Resource URI.");
            syntax.DefineOption("key", ref options.Path, $"File path to the account key to use.");
            syntax.DefineOption("verbose", ref options.Verbose, $"Print process log.");

            syntax.DefineEnumParameter("action", ref options.Action, "Order action");
            syntax.DefineParameter("name", ref options.Value, "Domain name");

            return options;
        }

        public async Task<object> Process()
        {
            switch (Args.Action)
            {
                case AzureAction.Dns:
                    return await SetDns();
            }

            throw new NotSupportedException();
        }

        private async Task<object> SetDns()
        {
            var key = await Args.LoadKey(true);

            Logger.Debug("Using ACME server {0}.", Args.Server);
            var ctx = ContextFactory.Create(Args.Server, key);

            var orderCtx = ctx.Order(Args.OrderUri);
            var authzCtx = await orderCtx.Authorization(Args.Value);
            if (authzCtx == null)
            {
                throw new Exception($"Authz not found for {Args.Value}.");
            }

            var authz = await authzCtx.Resource();
            var idValue = authz.Identifier.Value;

            var challengeCtx = await authzCtx.Dns();
            if (challengeCtx == null)
            {
                throw new Exception($"DNS challenge for {Args.Value} not found.");
            }

            var dnsValue = ctx.AccountKey.DnsTxtRecord(challengeCtx.Token);

            var credentials = GetAuzreCredentials();
            using (var client = ContextFactory.CreateDnsManagementClient(credentials))
            {
                client.SubscriptionId = Args.Subscription.ToString();

                ZoneInner idZone = null;
                var zones = await client.Zones.ListAsync();
                while (idZone == null && zones != null)
                {
                    foreach (var zone in zones)
                    {
                        if (idValue.EndsWith($".{zone.Name}"))
                        {
                            idZone = zone;
                            break;
                        }
                    }

                    zones = string.IsNullOrWhiteSpace(zones.NextPageLink) ? null : await client.Zones.ListNextAsync(zones.NextPageLink);
                }

                if (idZone == null)
                {
                    throw new Exception($"DNS zone for name {Args.Value} not found.");
                }
                else
                {
                    Logger.Debug("DNS zone:\n{0}", JsonConvert.SerializeObject(idZone, Formatting.Indented));
                }

                var name = "_acme-challenge." + idValue.Substring(0, idValue.Length - idZone.Name.Length - 1);
                Logger.Debug("Adding TXT record '{0}' for '{1}' in '{2}' zone.", dnsValue, name, idZone.Name);

                var recordSet = await client.RecordSets.CreateOrUpdateAsync(
                    Args.ResourceGroup,
                    idZone.Name,
                    name,
                    RecordType.TXT,
                    new RecordSetInner(
                        name: name,
                        tTL: 300,
                        txtRecords: new[] { new TxtRecord(new[] { dnsValue }) }));

                return new
                {
                    data = recordSet
                };
            }
        }

        private AzureCredentials GetAuzreCredentials()
        {
            var loginInfo = new ServicePrincipalLoginInformation
            {
                ClientId = Args.UserName,
                ClientSecret = Args.Password,
            };

            var env =
                Args.CloudEnvironment == AzureCloudEnvironment.China ? AzureEnvironment.AzureChinaCloud :
                Args.CloudEnvironment == AzureCloudEnvironment.German ? AzureEnvironment.AzureGermanCloud :
                Args.CloudEnvironment == AzureCloudEnvironment.USGovernment ? AzureEnvironment.AzureUSGovernment :
                AzureEnvironment.AzureGlobalCloud;

            var credentials = new AzureCredentials(loginInfo, Args.Talent.ToString(), env);
            return credentials;
        }
    }
}