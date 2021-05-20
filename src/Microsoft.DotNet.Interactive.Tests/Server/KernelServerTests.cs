﻿// Copyright (c) .NET Foundation and contributors. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reactive.Linq;
using FluentAssertions;
using System.Linq;
using System.Reactive.Concurrency;
using System.Reactive.Subjects;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.DotNet.Interactive.Commands;
using Microsoft.DotNet.Interactive.Connection;
using Microsoft.DotNet.Interactive.CSharp;
using Microsoft.DotNet.Interactive.Events;
using Microsoft.DotNet.Interactive.Formatting;
using Microsoft.DotNet.Interactive.Jupyter;
using Microsoft.DotNet.Interactive.Server;
using Microsoft.DotNet.Interactive.Tests.Utility;
using Pocket;
using Xunit;
using Xunit.Abstractions;

namespace Microsoft.DotNet.Interactive.Tests.Server
{

    public class KernelServerTests : IDisposable
    {
        private readonly CompositeDisposable _disposables = new();
        private readonly RecordingKernelCommandAndEventSender _serverOutputChannel;
        private readonly RecordingKernelCommandAndEventReceiver _serverInputChannel;
        private readonly CompositeKernel _kernel;

        private IList<IKernelEventEnvelope> KernelEvents => _serverOutputChannel.KernelEvents.ToList();

        public KernelServerTests(ITestOutputHelper output)
        {
            _kernel = new CompositeKernel
            {
                new CSharpKernel()
                    .UseKernelHelpers()
                    .UseNugetDirective()
                    .UseDefaultMagicCommands()
            };

            _serverOutputChannel = new RecordingKernelCommandAndEventSender();
            _serverInputChannel = new RecordingKernelCommandAndEventReceiver();
            var kernelServer = _kernel.CreateKernelServer(_serverInputChannel, _serverOutputChannel, new DirectoryInfo(Environment.CurrentDirectory));
            _kernel.RegisterForDisposal(kernelServer);
            var _ = kernelServer.RunAsync();

            _disposables.Add(output.SubscribeToPocketLogger());
            _disposables.Add(_kernel.LogEventsToPocketLogger());
            _disposables.Add(_kernel);
        }

        [Fact]
        public async Task It_produces_a_unique_CommandHandled_for_root_command()
        {
            var command = new SubmitCode("#!time\ndisplay(1543); display(4567);");
            command.SetToken("abc");

            _serverInputChannel.Send(command);

            await WaitForCompletion();

            KernelEvents
                .Should()
                .ContainSingle<KernelEventEnvelope<CommandSucceeded>>()
                .Which
                .Event
                .Command
                .GetToken()
                .Should()
                .Be("abc");
        }


        [Fact]
        public async Task It_does_not_publish_ReturnValueProduced_events_if_the_value_is_DisplayedValue()
        {
            _serverInputChannel.Send(new SubmitCode("display(1543)"));

            await WaitForCompletion();

            KernelEvents
                .Should()
                .NotContain(e => e.Event is ReturnValueProduced);
        }

        [Fact]
        public async Task It_publishes_diagnostic_events_on_json_parse_errors()
        {
            var invalidJson = "{ hello";

            _serverInputChannel.Send(invalidJson);

            await Task.Delay(1000);

            KernelEvents
                .Should()
                .ContainSingle<KernelEventEnvelope<DiagnosticLogEntryProduced>>()
                .Which
                .Event
                .Message
                .Should()
                .Contain(invalidJson);
        }

        [Fact]
        public async Task It_indicates_when_a_code_submission_is_incomplete()
        {
            var command = new SubmitCode(@"var a = 12");
            command.SetToken("abc");

            _serverInputChannel.Send(command);

            await WaitForCompletion();

            KernelEvents
                .Should()
                .ContainSingle<KernelEventEnvelope<IncompleteCodeSubmissionReceived>>(e => e.Event.Command.GetToken() == "abc");
        }

        [Fact]
        public async Task It_does_not_indicate_compilation_errors_as_exceptions()
        {
            var command = new SubmitCode("DOES NOT COMPILE");
            command.SetToken("abc");

            _serverInputChannel.Send(command);

            await WaitForCompletion();

            KernelEvents
                .Should()
                .ContainSingle<KernelEventEnvelope<CommandFailed>>()
                .Which
                .Event
                .Message
                .ToLowerInvariant()
                .Should()
                .NotContain("exception");
        }

        [Fact]
        public async Task It_can_eval_function_instances()
        {
            _serverInputChannel.Send(new SubmitCode(@"Func<int> func = () => 1;"));

            await WaitForCompletion();

            _serverInputChannel.Send(new SubmitCode(@"func()"));
            var kernelCommand = new SubmitCode(@"func");
            kernelCommand.SetToken("finalCommand");
            _serverInputChannel.Send(kernelCommand);

            await WaitForCompletion("finalCommand");

            KernelEvents
                .Count(e => e.Event is ReturnValueProduced)
                .Should()
                .Be(2);
        }

        private async Task WaitForCompletion()
        {
            await _kernel.KernelEvents.ObserveOn(TaskPoolScheduler.Default).FirstAsync(e => e is CommandSucceeded or CommandFailed);
        }

        private async Task WaitForCompletion(string commandToken)
        {
            await _kernel.KernelEvents.ObserveOn(TaskPoolScheduler.Default).FirstAsync(e => e is CommandSucceeded or CommandFailed && e.Command.GetToken() == commandToken);
        }

        [Fact]
        public async Task Kernel_can_pound_r_nuget_using_kernel_client()
        {
            var command = new SubmitCode(@"#r ""nuget:Microsoft.Spark, 0.4.0""");
            command.SetToken("abc");

            _serverInputChannel.Send(command);

            await WaitForCompletion();

            KernelEvents
                .Should()
                .ContainSingle<KernelEventEnvelope<PackageAdded>>(
                    where: e => e.Event.Command.GetToken() == "abc" &&
                                e.Event.PackageReference.PackageName == "Microsoft.Spark");
        }

        [Fact]
        public async Task it_produces_values_when_executing_Console_output()
        {
            var guid = Guid.NewGuid().ToString();

            var command = new SubmitCode($"Console.Write(\"{guid}\");");

            _serverInputChannel.Send(command);

            await WaitForCompletion();

            KernelEvents
                .Should()
                .ContainSingle<KernelEventEnvelope<StandardOutputValueProduced>>()
                .Which
                .Event
                .FormattedValues
                .Should()
                .ContainSingle(f => f.MimeType == PlainTextFormatter.MimeType &&
                                    f.Value.Equals(guid));
        }

        public void Dispose()
        {
            _disposables.Dispose();
        }

        class RecordingKernelCommandAndEventSender : IKernelCommandAndEventSender
        {
            private readonly ConcurrentQueue<IKernelCommandEnvelope> _commands;
            private readonly ConcurrentQueue<IKernelEventEnvelope> _events;
            public IEnumerable<IKernelEventEnvelope> KernelEvents => _events;
            public IEnumerable<IKernelCommandEnvelope> KernelCommands => _commands;

            public RecordingKernelCommandAndEventSender()
            {
                _commands = new ConcurrentQueue<IKernelCommandEnvelope>();
                _events = new ConcurrentQueue<IKernelEventEnvelope>();
            }
            public Task SendAsync(KernelCommand kernelCommand, CancellationToken cancellationToken)
            {
                _commands.Enqueue(KernelCommandEnvelope.Create(kernelCommand));
                return Task.CompletedTask;
            }

            public Task SendAsync(KernelEvent kernelEvent, CancellationToken cancellationToken)
            {
                _events.Enqueue(KernelEventEnvelope.Create(kernelEvent));
                return Task.CompletedTask;
            }
        }

        class RecordingKernelCommandAndEventReceiver : KernelCommandAndEventReceiverBase
        {
            private readonly Subject<string> _receiver = new();
            private readonly ConcurrentQueue<string> _queue;

            public RecordingKernelCommandAndEventReceiver()
            {
                _queue = new ConcurrentQueue<string>();
                _receiver.Subscribe(message =>
                {
                    _queue.Enqueue(message);
                });
            }

            public void Send(string kernelCommandOrEvent)
            {
                _receiver.OnNext(kernelCommandOrEvent);
            }

            public void Send(KernelCommand kernelCommand)
            {
                _receiver.OnNext(KernelCommandEnvelope.Serialize(KernelCommandEnvelope.Create(kernelCommand)));
            }

            public void Send(KernelEvent kernelEvent)
            {
                _receiver.OnNext(KernelEventEnvelope.Serialize(KernelEventEnvelope.Create(kernelEvent)));
            }

            protected override async Task<string> ReadMessageAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_queue.TryDequeue(out var message))
                {
                    return message;
                }
                await _receiver.FirstAsync();
                _queue.TryDequeue(out message);
                return message;
            }
        }
    }
}