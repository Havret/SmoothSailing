﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SmoothSailing;

public class Release : IAsyncDisposable
{
    public string DeploymentName { get; }
    private readonly IProcessLauncher _processExecutor;
    private readonly KubernetesContext? _kubernetesContext;
    private readonly List<(Task, CancellationTokenSource)> _portForwards = new();

    internal Release(string deploymentName, IProcessLauncher processExecutor, KubernetesContext? kubernetesContext)
    {
        DeploymentName = deploymentName;
        _processExecutor = processExecutor;
        _kubernetesContext = kubernetesContext;
    }

    public async Task<int> StartPortForwardForService(string serviceName, int servicePort, int? localPort = null) 
        => await StartPortForwardFor("service", serviceName, servicePort, localPort);
    
    public async Task<int> StartPortForwardForPod(string serviceName, int servicePort, int? localPort = null) 
        => await StartPortForwardFor("pod", serviceName, servicePort, localPort);

    private async Task<int> StartPortForwardFor(string elementType, string elementName, int servicePort, int? localPort)
    {
        var cancellationTokenSource = new CancellationTokenSource();
        var portForwardParameters = new HelmCommandParameterBuilder();
        portForwardParameters.ApplyContextInfo(_kubernetesContext);
        var asyncEnumerable = _processExecutor.Execute("kubectl", $"port-forward {elementType}/{elementName} {localPort}:{servicePort} {portForwardParameters.Build()}", cancellationTokenSource.Token);

        var enumerator = asyncEnumerable.GetAsyncEnumerator(default);
        await enumerator.MoveNextAsync();
        if (enumerator.Current.StartsWith("Forwarding from"))
        {
            _portForwards.Add((ReadToEnd(enumerator), cancellationTokenSource));
            return ExtractPortNumber(enumerator.Current);
        }

        await ReadToEnd(enumerator);
        return 0;
    }

    private static int ExtractPortNumber(string input)
    {
        var pattern = @":(\d+) ->";
        var match = Regex.Match(input, pattern);

        if (match.Success)
        {
            var portNumber = match.Groups[1].Value;
            return int.Parse(portNumber);
        }

        throw new ArgumentException("Invalid input. No port number found.");
    }

    private async Task ReadToEnd(IAsyncEnumerator<string> enumerator)
    {
        while (await enumerator.MoveNextAsync()){}
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            foreach (var (_, cts) in _portForwards)
            {
                cts.Cancel();
            }

            await Task.WhenAll(_portForwards.Select(x => x.Item1).ToArray());
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
        }

        var uninstallParameters = new HelmCommandParameterBuilder();
        uninstallParameters.ApplyContextInfo(_kubernetesContext);
        await _processExecutor.ExecuteToEnd("helm", $"uninstall {DeploymentName} {uninstallParameters.Build()}", default);
    }
}
