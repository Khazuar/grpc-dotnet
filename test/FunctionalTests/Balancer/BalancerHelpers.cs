#region Copyright notice and license

// Copyright 2019 The gRPC Authors
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

#endregion

#if SUPPORT_LOAD_BALANCING
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;
using FunctionalTestsWebsite;
using Google.Protobuf;
using Grpc.AspNetCore.FunctionalTests.Infrastructure;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Balancer;
using Grpc.Net.Client.Balancer.Internal;
using Grpc.Net.Client.Configuration;
using Grpc.Net.Client.Internal;
using Grpc.Tests.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Grpc.AspNetCore.FunctionalTests.Balancer;

internal static class BalancerHelpers
{
    public static EndpointContext<TRequest, TResponse> CreateGrpcEndpoint<TRequest, TResponse>(
        int port,
        UnaryServerMethod<TRequest, TResponse> callHandler,
        string methodName,
        HttpProtocols? protocols = null,
        bool? isHttps = null,
        X509Certificate2? certificate = null)
        where TRequest : class, IMessage, new()
        where TResponse : class, IMessage, new()
    {
        var server = CreateServer(port, protocols, isHttps, certificate);
        var method = server.DynamicGrpc.AddUnaryMethod(callHandler, methodName);
        var url = server.GetUrl(isHttps.GetValueOrDefault(false) ? TestServerEndpointName.Http2WithTls : TestServerEndpointName.Http2);

        return new EndpointContext<TRequest, TResponse>(server, method, url);
    }

    public class EndpointContext<TRequest, TResponse> : IDisposable
        where TRequest : class, IMessage, new()
        where TResponse : class, IMessage, new()
    {
        private readonly GrpcTestFixture<Startup> _server;

        public EndpointContext(GrpcTestFixture<Startup> server, Method<TRequest, TResponse> method, Uri address)
        {
            _server = server;
            Method = method;
            Address = address;
        }

        public Method<TRequest, TResponse> Method { get; }
        public Uri Address { get; }
        public ILoggerFactory LoggerFactory => _server.LoggerFactory;
        public EndPoint EndPoint => new DnsEndPoint(Address.Host, Address.Port);

        public void Dispose()
        {
            _server.Dispose();
        }
    }

    public static GrpcTestFixture<Startup> CreateServer(int port, HttpProtocols? protocols = null, bool? isHttps = null, X509Certificate2? certificate = null)
    {
        var endpointName = isHttps.GetValueOrDefault(false) ? TestServerEndpointName.Http2WithTls : TestServerEndpointName.Http2;

        return new GrpcTestFixture<Startup>(
            null,
            (options, urls) =>
            {
                urls[endpointName] = isHttps.GetValueOrDefault(false)
                    ? $"https://127.0.0.1:{port}"
                    : $"http://127.0.0.1:{port}";
                options.ListenLocalhost(port, listenOptions =>
                {
                    listenOptions.Protocols = protocols ?? HttpProtocols.Http2;

                    if (isHttps.GetValueOrDefault(false))
                    {
                        if (certificate == null)
                        {
                            var basePath = Path.GetDirectoryName(typeof(InProcessTestServer).Assembly.Location);
                            var certPath = Path.Combine(basePath!, "server1.pfx");
                            listenOptions.UseHttps(certPath, "1111");
                        }
                        else
                        {
                            listenOptions.UseHttps(certificate);
                        }
                    }
                });
            },
            endpointName);
    }

    public static Task<GrpcChannel> CreateChannel(
        ILoggerFactory loggerFactory,
        LoadBalancingConfig? loadBalancingConfig,
        Uri[] endpoints,
        HttpMessageHandler? httpMessageHandler = null,
        bool? connect = null,
        RetryPolicy? retryPolicy = null,
        Func<Socket, DnsEndPoint, CancellationToken, ValueTask>? socketConnect = null,
        TimeSpan? connectTimeout = null)
    {
        var resolver = new TestResolver();
        var e = endpoints.Select(i => new BalancerAddress(i.Host, i.Port)).ToList();
        resolver.UpdateAddresses(e);

        return CreateChannel(loggerFactory, loadBalancingConfig, resolver, httpMessageHandler, connect, retryPolicy, socketConnect, connectTimeout);
    }

    public static async Task<GrpcChannel> CreateChannel(
        ILoggerFactory loggerFactory,
        LoadBalancingConfig? loadBalancingConfig,
        TestResolver resolver,
        HttpMessageHandler? httpMessageHandler = null,
        bool? connect = null,
        RetryPolicy? retryPolicy = null,
        Func<Socket, DnsEndPoint, CancellationToken, ValueTask>? socketConnect = null,
        TimeSpan? connectTimeout = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ResolverFactory>(new TestResolverFactory(resolver));
        services.AddSingleton<IRandomGenerator>(new TestRandomGenerator());
        services.AddSingleton<ISubchannelTransportFactory>(new TestSubchannelTransportFactory(TimeSpan.FromSeconds(0.5), connectTimeout, socketConnect));
        services.AddSingleton<LoadBalancerFactory>(new LeastUsedBalancerFactory());

        var serviceConfig = new ServiceConfig();
        if (loadBalancingConfig != null)
        {
            serviceConfig.LoadBalancingConfigs.Add(loadBalancingConfig);
        }
        if (retryPolicy != null)
        {
            serviceConfig.MethodConfigs.Add(new MethodConfig
            {
                Names = { MethodName.Default },
                RetryPolicy = retryPolicy
            });
        }

        var channel = GrpcChannel.ForAddress("test:///localhost", new GrpcChannelOptions
        {
            LoggerFactory = loggerFactory,
            Credentials = ChannelCredentials.Insecure,
            ServiceProvider = services.BuildServiceProvider(),
            ServiceConfig = serviceConfig,
            HttpHandler = httpMessageHandler
        });

        if (connect ?? false)
        {
            await channel.ConnectAsync();
        }

        return channel;
    }

    public static T? GetInnerLoadBalancer<T>(GrpcChannel channel) where T : LoadBalancer
    {
        var balancer = (ChildHandlerLoadBalancer)channel.ConnectionManager._balancer!;
        return (T?)balancer._current?.LoadBalancer;
    }

    private class TestRandomGenerator : IRandomGenerator
    {
        public int Next(int minValue, int maxValue)
        {
            return 0;
        }

        public double NextDouble()
        {
            return 0;
        }
    }

    internal class TestSubchannelTransportFactory : ISubchannelTransportFactory
    {
        private readonly TimeSpan _socketPingInterval;
        private readonly TimeSpan? _connectTimeout;
        private readonly Func<Socket, DnsEndPoint, CancellationToken, ValueTask>? _socketConnect;

        public TestSubchannelTransportFactory(TimeSpan socketPingInterval, TimeSpan? connectTimeout, Func<Socket, DnsEndPoint, CancellationToken, ValueTask>? socketConnect)
        {
            _socketPingInterval = socketPingInterval;
            _connectTimeout = connectTimeout;
            _socketConnect = socketConnect;
        }

        public ISubchannelTransport Create(Subchannel subchannel)
        {
#if NET5_0_OR_GREATER
            return new SocketConnectivitySubchannelTransport(
                subchannel,
                _socketPingInterval,
                _connectTimeout,
                subchannel._manager.LoggerFactory,
                _socketConnect);
#else
            return new PassiveSubchannelTransport(subchannel);
#endif
        }
    }
}
#endif
