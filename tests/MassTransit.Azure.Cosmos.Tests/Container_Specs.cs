namespace MassTransit.Azure.Cosmos.Tests
{
    namespace ContainerTests
    {
        using System;
        using System.Threading.Tasks;
        using Microsoft.Extensions.DependencyInjection;
        using NUnit.Framework;
        using TestFramework;
        using TestFramework.Sagas;
        using Testing;


        public class Using_the_container_integration :
            InMemoryTestFixture
        {
            readonly IServiceProvider _provider;

            public Using_the_container_integration()
            {
                _provider = new ServiceCollection()
                    .AddMassTransit(configurator =>
                    {
                        configurator.AddSagaStateMachine<TestStateMachineSaga, TestInstance>()
                            .CosmosRepository(r =>
                            {
                                r.EndpointUri = Configuration.EndpointUri;
                                r.Key = Configuration.Key;

                                r.DatabaseId = "sagaTest";
                                r.CollectionId = "TestInstance";
                            });

                        configurator.AddBus(provider => BusControl);
                    })
                    .AddScoped<PublishTestStartedActivity>().BuildServiceProvider();
            }

            [Test]
            public async Task Should_work_as_expected()
            {
                Task<ConsumeContext<TestStarted>> started = await ConnectPublishHandler<TestStarted>();
                Task<ConsumeContext<TestUpdated>> updated = await ConnectPublishHandler<TestUpdated>();

                var correlationId = NewId.NextGuid();

                await InputQueueSendEndpoint.Send(new StartTest
                {
                    CorrelationId = correlationId,
                    TestKey = "Unique"
                });

                await started;

                await InputQueueSendEndpoint.Send(new UpdateTest
                {
                    TestId = correlationId,
                    TestKey = "Unique"
                });

                await updated;
            }

            protected override void ConfigureInMemoryReceiveEndpoint(IInMemoryReceiveEndpointConfigurator configurator)
            {
                configurator.UseInMemoryOutbox();
                configurator.ConfigureSaga<TestInstance>(_provider.GetRequiredService<IBusRegistrationContext>());
            }
        }


        [TestFixture]
        public class Testing_with_the_container
        {
            [Test]
            public async Task Should_work_as_expected()
            {
                await using var provider = new ServiceCollection()
                    .AddMassTransitTestHarness(x =>
                    {
                        x.AddSagaStateMachine<TestStateMachineSaga, TestInstance>()
                            .CosmosRepository(r =>
                            {
                                r.EndpointUri = Configuration.EndpointUri;
                                r.Key = Configuration.Key;

                                r.DatabaseId = "sagaTest";
                                r.CollectionId = "TestInstance";
                            });

                        x.AddConfigureEndpointsCallback((name, configurator) =>
                        {
                            configurator.UseInMemoryOutbox();
                        });
                    })
                    .AddScoped<PublishTestStartedActivity>()
                    .BuildServiceProvider(true);

                var harness = provider.GetRequiredService<ITestHarness>();

                await harness.Start();

                var correlationId = NewId.NextGuid();

                await harness.Bus.Publish(new StartTest
                {
                    CorrelationId = correlationId,
                    TestKey = "Unique"
                });

                Assert.IsTrue(await harness.Published.Any<TestStarted>(x => x.Context.Message.CorrelationId == correlationId));

                // For some reason, the LINQ provider for Cosmos does not properly resolve this query.
                 var sagaHarness = harness.GetSagaStateMachineHarness<TestStateMachineSaga, TestInstance>();
                // Assert.IsNotNull(await sagaHarness.Exists(correlationId, x => x.Active));

                await harness.Bus.Publish(new UpdateTest
                {
                    TestId = correlationId,
                    TestKey = "Unique"
                });

                Assert.IsTrue(await harness.Published.Any<TestUpdated>(x => x.Context.Message.CorrelationId == correlationId));
            }
        }


        public class Using_the_container_integration_with_client_factory :
            InMemoryTestFixture
        {
            readonly IServiceProvider _provider;

            public Using_the_container_integration_with_client_factory()
            {
                var clientName = Guid.NewGuid().ToString();

                _provider = new ServiceCollection()
                    .AddCosmosClientFactory(Configuration.EndpointUri, Configuration.Key)
                    .AddMassTransit(configurator =>
                    {
                        configurator.AddSagaStateMachine<TestStateMachineSaga, TestInstance>()
                            .CosmosRepository(r =>
                            {
                                r.DatabaseId = "sagaTest";
                                r.CollectionId = "TestInstance";
                                r.UseClientFactory(clientName);
                            });

                        configurator.AddBus(provider => BusControl);
                    })
                    .AddScoped<PublishTestStartedActivity>().BuildServiceProvider();
            }

            [Test]
            public async Task Should_work_as_expected()
            {
                Task<ConsumeContext<TestStarted>> started = await ConnectPublishHandler<TestStarted>();
                Task<ConsumeContext<TestUpdated>> updated = await ConnectPublishHandler<TestUpdated>();

                var correlationId = NewId.NextGuid();

                await InputQueueSendEndpoint.Send(new StartTest
                {
                    CorrelationId = correlationId,
                    TestKey = "Unique"
                });

                await started;

                await InputQueueSendEndpoint.Send(new UpdateTest
                {
                    TestId = correlationId,
                    TestKey = "Unique"
                });

                await updated;
            }

            protected override void ConfigureInMemoryReceiveEndpoint(IInMemoryReceiveEndpointConfigurator configurator)
            {
                configurator.UseInMemoryOutbox();
                configurator.ConfigureSaga<TestInstance>(_provider.GetRequiredService<IBusRegistrationContext>());
            }
        }


        public class TestInstance :
            SagaStateMachineInstance
        {
            public string CurrentState { get; set; }
            public string Key { get; set; }

            public Guid CorrelationId { get; set; }
        }


        public class TestStateMachineSaga :
            MassTransitStateMachine<TestInstance>
        {
            public TestStateMachineSaga()
            {
                InstanceState(x => x.CurrentState);

                Event(() => Updated, x => x.CorrelateById(m => m.Message.TestId));

                Initially(
                    When(Started)
                        .Then(context => context.Instance.Key = context.Data.TestKey)
                        .Activity(x => x.OfInstanceType<PublishTestStartedActivity>())
                        .TransitionTo(Active));

                During(Active,
                    When(Updated)
                        .Publish(context => new TestUpdated
                        {
                            CorrelationId = context.Instance.CorrelationId,
                            TestKey = context.Instance.Key
                        })
                        .TransitionTo(Done)
                        .Finalize());

                SetCompletedWhenFinalized();
            }

            public State Active { get; private set; }
            public State Done { get; private set; }

            public Event<StartTest> Started { get; private set; }
            public Event<UpdateTest> Updated { get; private set; }
        }


        public class UpdateTest
        {
            public Guid TestId { get; set; }
            public string TestKey { get; set; }
        }


        public class PublishTestStartedActivity :
            IStateMachineActivity<TestInstance>
        {
            readonly ConsumeContext _context;

            public PublishTestStartedActivity(ConsumeContext context)
            {
                _context = context;
            }

            public void Probe(ProbeContext context)
            {
                context.CreateScope("publisher");
            }

            public void Accept(StateMachineVisitor visitor)
            {
                visitor.Visit(this);
            }

            public async Task Execute(BehaviorContext<TestInstance> context, IBehavior<TestInstance> next)
            {
                await _context.Publish(new TestStarted
                {
                    CorrelationId = context.Instance.CorrelationId,
                    TestKey = context.Instance.Key
                }).ConfigureAwait(false);

                await next.Execute(context).ConfigureAwait(false);
            }

            public async Task Execute<T>(BehaviorContext<TestInstance, T> context, IBehavior<TestInstance, T> next)
                where T : class
            {
                await _context.Publish(new TestStarted
                {
                    CorrelationId = context.Instance.CorrelationId,
                    TestKey = context.Instance.Key
                }).ConfigureAwait(false);

                await next.Execute(context).ConfigureAwait(false);
            }

            public Task Faulted<TException>(BehaviorExceptionContext<TestInstance, TException> context, IBehavior<TestInstance> next)
                where TException : Exception
            {
                return next.Faulted(context);
            }

            public Task Faulted<T, TException>(BehaviorExceptionContext<TestInstance, T, TException> context, IBehavior<TestInstance, T> next)
                where TException : Exception
                where T : class
            {
                return next.Faulted(context);
            }
        }
    }
}
