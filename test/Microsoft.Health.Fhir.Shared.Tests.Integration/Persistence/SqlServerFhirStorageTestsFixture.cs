// -------------------------------------------------------------------------------------------------
// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License (MIT). See LICENSE in the repo root for license information.
// -------------------------------------------------------------------------------------------------

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading;
using Azure.Identity;
using Ignixa.Specification.Extensions;
using Medino;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Health.Abstractions.Features.Transactions;
using Microsoft.Health.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Configs;
using Microsoft.Health.Fhir.Core.Features.Context;
using Microsoft.Health.Fhir.Core.Features.Definition;
using Microsoft.Health.Fhir.Core.Features.Operations;
using Microsoft.Health.Fhir.Core.Features.Persistence;
using Microsoft.Health.Fhir.Core.Features.Persistence.Orchestration;
using Microsoft.Health.Fhir.Core.Features.Search;
using Microsoft.Health.Fhir.Core.Features.Search.Access;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions;
using Microsoft.Health.Fhir.Core.Features.Search.Expressions.Parsers;
using Microsoft.Health.Fhir.Core.Features.Search.Parameters;
using Microsoft.Health.Fhir.Core.Features.Search.Registry;
using Microsoft.Health.Fhir.Core.Features.Search.SearchValues;
using Microsoft.Health.Fhir.Core.Models;
using Microsoft.Health.Fhir.Core.UnitTests.Extensions;
using Microsoft.Health.Fhir.SqlServer.Features.Schema;
using Microsoft.Health.Fhir.SqlServer.Features.Schema.Model;
using Microsoft.Health.Fhir.SqlServer.Features.Search;
using Microsoft.Health.Fhir.SqlServer.Features.Search.Expressions.Visitors;
using Microsoft.Health.Fhir.SqlServer.Features.Search.Ignixa;
using Microsoft.Health.Fhir.SqlServer.Features.Storage;
using Microsoft.Health.Fhir.SqlServer.Features.Storage.Registry;
using Microsoft.Health.Fhir.SqlServer.Registration;
using Microsoft.Health.Fhir.Tests.Common;
using Microsoft.Health.JobManagement;
using Microsoft.Health.JobManagement.UnitTests;
using Microsoft.Health.SqlServer;
using Microsoft.Health.SqlServer.Configs;
using Microsoft.Health.SqlServer.Features.Client;
using Microsoft.Health.SqlServer.Features.Schema;
using Microsoft.Health.SqlServer.Features.Schema.Manager;
using Microsoft.Health.SqlServer.Features.Storage;
using NSubstitute;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace Microsoft.Health.Fhir.Tests.Integration.Persistence
{
    public class SqlServerFhirStorageTestsFixture : IServiceProvider, IAsyncLifetime
    {
        private const string LocalConnectionString = "server=(local);Integrated Security=true;TrustServerCertificate=True";
        private const string MasterDatabaseName = "master";

        private readonly string _initialConnectionString;
        private readonly IOptions<CoreFeatureConfiguration> _options;
        private readonly int _maximumSupportedSchemaVersion;
        private readonly string _databaseName;
        private readonly RequestContextAccessor<IFhirRequestContext> _fhirRequestContextAccessor = Substitute.For<RequestContextAccessor<IFhirRequestContext>>();

        private IMediator _mediator;
        private SqlServerFhirDataStore _fhirDataStore;
        private IFhirOperationDataStore _fhirOperationDataStore;
        private SqlServerFhirOperationDataStore _sqlServerFhirOperationDataStore;
        private SqlServerFhirStorageTestHelper _testHelper;
        private SchemaUpgradeRunner _schemaUpgradeRunner;
        private FilebasedSearchParameterStatusDataStore _filebasedSearchParameterStatusDataStore;
        private ISearchService _searchService;
        private SearchParameterDefinitionManager _searchParameterDefinitionManager;
        private SupportedSearchParameterDefinitionManager _supportedSearchParameterDefinitionManager;
        private SearchParameterStatusManager _searchParameterStatusManager;
        private SqlQueueClient _sqlQueueClient;
        private FhirSqlServerConfiguration _fhirSqlConfiguration;

        public SqlServerFhirStorageTestsFixture()
            : this(SchemaVersionConstants.Max, GetDatabaseName())
        {
        }

        internal SqlServerFhirStorageTestsFixture(int maximumSupportedSchemaVersion, string databaseName, IOptions<CoreFeatureConfiguration> coreFeatures = null)
        {
            _initialConnectionString = EnvironmentVariables.GetEnvironmentVariable(KnownEnvironmentVariableNames.SqlServerConnectionString);
            _maximumSupportedSchemaVersion = maximumSupportedSchemaVersion;
            _databaseName = databaseName;
            TestConnectionString = new SqlConnectionStringBuilder(_initialConnectionString) { InitialCatalog = _databaseName, Encrypt = true }.ToString();

            var schemaOptions = new SqlServerSchemaOptions { AutomaticUpdatesEnabled = true };
            SqlServerDataStoreConfiguration = Options.Create(new SqlServerDataStoreConfiguration
            {
                ConnectionString = TestConnectionString,
                Initialize = true,
                SchemaOptions = schemaOptions,
                StatementTimeout = TimeSpan.FromMinutes(10),
                CommandTimeout = TimeSpan.FromMinutes(3),
            });

            SchemaInformation = new SchemaInformation(SchemaVersionConstants.Min, maximumSupportedSchemaVersion);

            _options = coreFeatures ?? Options.Create(new CoreFeatureConfiguration());
        }

        public string TestConnectionString { get; private set; }

        internal SqlServerFhirOperationDataStore SqlServerOperationDataStore => _sqlServerFhirOperationDataStore;

        internal SqlTransactionHandler SqlTransactionHandler { get; private set; }

        internal SqlConnectionWrapperFactory SqlConnectionWrapperFactory { get; private set; }

        internal SqlServerFhirDataStore SqlServerFhirDataStore => _fhirDataStore;

        internal IOptions<SqlServerDataStoreConfiguration> SqlServerDataStoreConfiguration { get; private set; }

        internal ISqlConnectionBuilder SqlConnectionBuilder { get; private set; }

        internal SqlRetryService SqlRetryService { get; private set; }

        internal SqlServerSearchParameterStatusDataStore SqlServerSearchParameterStatusDataStore { get; private set; }

        internal SqlServerFhirModel SqlServerFhirModel { get; private set; }

        internal SchemaInformation SchemaInformation { get; private set; }

        internal ISqlQueryHashCalculator SqlQueryHashCalculator { get; private set; }

        // A dedicated search service wired with the real Ignixa search-options adapter and a real
        // compile-only router configured to execute the Ignixa-emitted SQL. The shared _searchService
        // deliberately keeps the baseline (substituted) Ignixa wiring so the rest of the suite is
        // unaffected; this parallel service exists solely to prove the Ignixa execution path runs real
        // SQL against the live database for eligible searches.
        internal SqlServerSearchService IgnixaSearchService { get; private set; }

        /// <summary>
        /// Every message the Ignixa router logged, newest last. The router logs a specific
        /// <c>Reason=</c> for each eligibility gate it closes and a <c>Stage=/Kind=</c> for each compiler
        /// capability gap, so a differential that unexpectedly falls back to legacy can report *why* instead of
        /// only that it did. Shared across the class, so a test that wants a clean window should clear it first.
        /// </summary>
        internal ConcurrentQueue<string> IgnixaRouterLog { get; } = new ConcurrentQueue<string>();

        /// <summary>
        /// The request context both search services read. Exposed so a test can install an
        /// <see cref="AccessControlContext"/> and drive the SMART clinical-scope path, which is otherwise
        /// unreachable from an integration test: the scopes live on the request context, not in the query string.
        /// A test that sets this must clear it again, since the fixture is shared across the class.
        /// </summary>
        internal RequestContextAccessor<IFhirRequestContext> FhirRequestContextAccessor => _fhirRequestContextAccessor;

        internal static string GetDatabaseName(string test = null)
        {
            return $"{ModelInfoProvider.Version}{(test == null ? string.Empty : $"_{test}")}_{DateTimeOffset.UtcNow.ToString("s").Replace("-", string.Empty).Replace(":", string.Empty)}_{Guid.NewGuid().ToString().Replace("-", string.Empty)}";
        }

        public async Task InitializeAsync()
        {
            _mediator = Substitute.For<IMediator>();

            var scriptProvider = new ScriptProvider<SchemaVersion>();
            var baseScriptProvider = new BaseScriptProvider();
            var sqlSortingValidator = new SqlServerSortingValidator(SchemaInformation);
            SqlRetryLogicBaseProvider sqlRetryLogicBaseProvider = SqlConfigurableRetryFactory.CreateFixedRetryProvider(new SqlClientRetryOptions().Settings);

            SqlConnectionBuilder = new DefaultSqlConnectionBuilder(SqlServerDataStoreConfiguration, sqlRetryLogicBaseProvider);

            var sqlConnection = Substitute.For<ISqlConnectionBuilder>();

            sqlConnection.GetSqlConnectionAsync(Arg.Any<string>(), Arg.Any<int?>(), Arg.Any<CancellationToken>()).ReturnsForAnyArgs((x) => Task.FromResult(GetSqlConnection(TestConnectionString)));
            var sqlConnectionWrapperFactory = new SqlConnectionWrapperFactory(new SqlTransactionHandler(), SqlConnectionBuilder, sqlRetryLogicBaseProvider, SqlServerDataStoreConfiguration);
            var schemaManagerDataStore = new SchemaManagerDataStore(sqlConnectionWrapperFactory, SqlServerDataStoreConfiguration, NullLogger<SchemaManagerDataStore>.Instance);
            _schemaUpgradeRunner = new SchemaUpgradeRunner(scriptProvider, baseScriptProvider, NullLogger<SchemaUpgradeRunner>.Instance, sqlConnectionWrapperFactory, schemaManagerDataStore);

            var searchParameterComparer = Substitute.For<ISearchParameterComparer<SearchParameterInfo>>();
            var statusDataStore = Substitute.For<ISearchParameterStatusDataStore>();
            var fhirDataStore = Substitute.For<IFhirDataStore>();
            _searchParameterDefinitionManager = new SearchParameterDefinitionManager(
                ModelInfoProvider.Instance,
                _mediator,
                CreateMockedScopeExtensions.CreateMockScopeProvider(() => _searchService),
                searchParameterComparer,
                statusDataStore.CreateMockScopeProvider(),
                fhirDataStore.CreateMockScopeProvider(),
                NullLogger<SearchParameterDefinitionManager>.Instance);

            _filebasedSearchParameterStatusDataStore = new FilebasedSearchParameterStatusDataStore(_searchParameterDefinitionManager, ModelInfoProvider.Instance);

            var securityConfiguration = new SecurityConfiguration { PrincipalClaims = { "oid" } };

            SqlTransactionHandler = new SqlTransactionHandler();
            SqlConnectionWrapperFactory = new SqlConnectionWrapperFactory(SqlTransactionHandler, SqlConnectionBuilder, sqlRetryLogicBaseProvider, SqlServerDataStoreConfiguration);
            SqlRetryService = new SqlRetryService(SqlConnectionBuilder, SqlServerDataStoreConfiguration, Options.Create(new SqlRetryServiceOptions()), new SqlRetryServiceDelegateOptions(), Options.Create(new CoreFeatureConfiguration()));

            var sqlServerFhirModel = new SqlServerFhirModel(
                SchemaInformation,
                _searchParameterDefinitionManager,
                () => _filebasedSearchParameterStatusDataStore,
                Options.Create(securityConfiguration),
                SqlConnectionWrapperFactory.CreateMockScopeProvider(),
                Substitute.For<IMediator>(),
                SqlRetryService,
                NullLogger<SqlServerFhirModel>.Instance);
            SqlServerFhirModel = sqlServerFhirModel;

            // the test queue client may not be enough for these tests. will need to look back into this.
            var queueClient = new TestQueueClient();

            // Add custom logic to set up the AzurePipelinesCredential if we are running in Azure Pipelines
            string federatedClientID = EnvironmentVariables.GetEnvironmentVariable(KnownEnvironmentVariableNames.AzureSubscriptionClientId);
            string federatedTenantId = EnvironmentVariables.GetEnvironmentVariable(KnownEnvironmentVariableNames.AzureSubscriptionTenantId);
            string serviceConnectionId = EnvironmentVariables.GetEnvironmentVariable(KnownEnvironmentVariableNames.AzureSubscriptionServiceConnectionId);
            string systemAccessToken = EnvironmentVariables.GetEnvironmentVariable(KnownEnvironmentVariableNames.SystemAccessToken);

            if (!string.IsNullOrEmpty(federatedClientID) && !string.IsNullOrEmpty(federatedTenantId) && !string.IsNullOrEmpty(serviceConnectionId) && !string.IsNullOrEmpty(systemAccessToken))
            {
                AzurePipelinesCredential azurePipelinesCredential = new(federatedTenantId, federatedClientID, serviceConnectionId, systemAccessToken);
                SqlAuthenticationProvider.SetProvider(SqlAuthenticationMethod.ActiveDirectoryWorkloadIdentity, new SqlAzurePipelinesWorkloadIdentityAuthenticationProvider(azurePipelinesCredential));
            }

            _testHelper = new SqlServerFhirStorageTestHelper(_initialConnectionString, MasterDatabaseName, sqlServerFhirModel, SqlConnectionBuilder, queueClient, SchemaInformation);
            await _testHelper.CreateAndInitializeDatabase(_databaseName, _maximumSupportedSchemaVersion, CancellationToken.None);

            var searchParameterToSearchValueTypeMap = new SearchParameterToSearchValueTypeMap();

            var serviceCollection = new ServiceCollection();
            serviceCollection.AddSqlServerTableRowParameterGenerators();
            serviceCollection.AddSingleton(sqlServerFhirModel);
            serviceCollection.AddSingleton<ISqlServerFhirModel>(sqlServerFhirModel);
            serviceCollection.AddSingleton(searchParameterToSearchValueTypeMap);
            var converter = (ICompressedRawResourceConverter)new CompressedRawResourceConverter();
            serviceCollection.AddSingleton(converter);

            ServiceProvider serviceProvider = serviceCollection.BuildServiceProvider();

            _supportedSearchParameterDefinitionManager = new SupportedSearchParameterDefinitionManager(_searchParameterDefinitionManager);

            SqlServerSearchParameterStatusDataStore = new SqlServerSearchParameterStatusDataStore(
                SqlRetryService,
                SchemaInformation,
                sqlServerFhirModel,
                _searchParameterDefinitionManager,
                NullLogger<SqlServerSearchParameterStatusDataStore>.Instance);

            var bundleConfiguration = new BundleConfiguration() { SupportsBundleOrchestrator = true };
            var bundleOptions = Substitute.For<IOptions<BundleConfiguration>>();
            bundleOptions.Value.Returns(bundleConfiguration);

            var bundleOrchestrator = new BundleOrchestrator(bundleOptions, NullLogger<BundleOrchestrator>.Instance);

            var importErrorSerializer = new Shared.Core.Features.Operations.Import.ImportErrorSerializer(new Hl7.Fhir.Serialization.FhirJsonSerializer());

            _fhirDataStore = new SqlServerFhirDataStore(
                sqlServerFhirModel,
                searchParameterToSearchValueTypeMap,
                _options,
                bundleOrchestrator,
                SqlRetryService,
                SqlConnectionWrapperFactory,
                SqlTransactionHandler,
                converter,
                NullLogger<SqlServerFhirDataStore>.Instance,
                SchemaInformation,
                ModelInfoProvider.Instance,
                _fhirRequestContextAccessor,
                importErrorSerializer,
                new SqlStoreClient(SqlRetryService, NullLogger<SqlStoreClient>.Instance, SchemaInformation));

            _fhirOperationDataStore = new SqlServerFhirOperationDataStore(SqlConnectionWrapperFactory, queueClient, NullLogger<SqlServerFhirOperationDataStore>.Instance, NullLoggerFactory.Instance);

            var sqlQueueClient = new SqlQueueClient(SchemaInformation, SqlRetryService, NullLogger<SqlQueueClient>.Instance);
            _sqlServerFhirOperationDataStore = new SqlServerFhirOperationDataStore(SqlConnectionWrapperFactory, sqlQueueClient, NullLogger<SqlServerFhirOperationDataStore>.Instance, NullLoggerFactory.Instance);

            _fhirRequestContextAccessor.RequestContext.CorrelationId.Returns(Guid.NewGuid().ToString());
            _fhirRequestContextAccessor.RequestContext.RouteName.Returns("routeName");

            var searchableSearchParameterDefinitionManager = new SearchableSearchParameterDefinitionManager(_searchParameterDefinitionManager, _fhirRequestContextAccessor);
            var instanceConfiguration = new FhirServerInstanceConfiguration();
            var searchParameterExpressionParser = new SearchParameterExpressionParser(new ReferenceSearchValueParser(_fhirRequestContextAccessor, instanceConfiguration));
            var expressionParser = new ExpressionParser(() => searchableSearchParameterDefinitionManager, searchParameterExpressionParser);

            // Cutover step 1: the shared search service keeps the baseline (substituted) Ignixa wiring so
            // the existing integration suite is unaffected. A dedicated IgnixaSearchService (built below) is
            // wired with the real adapter + router to prove the Ignixa execution path.
            IIgnixaSearchOptionsAdapter ignixaSearchOptionsAdapter = Substitute.For<IIgnixaSearchOptionsAdapter>();

            var searchOptionsFactory = new SearchOptionsFactory(
                expressionParser,
                () => searchableSearchParameterDefinitionManager,
                _options,
                _fhirRequestContextAccessor,
                sqlSortingValidator,
                new ExpressionAccessControl(_fhirRequestContextAccessor),
                ignixaSearchOptionsAdapter,
                new IgnixaSearchTenantAccessor(_fhirRequestContextAccessor),
                NullLogger<SearchOptionsFactory>.Instance);

            var searchParamTableExpressionQueryGeneratorFactory = new SearchParamTableExpressionQueryGeneratorFactory(searchParameterToSearchValueTypeMap);
            var sqlRootExpressionRewriter = new SqlRootExpressionRewriter(searchParamTableExpressionQueryGeneratorFactory);
            var chainFlatteningRewriter = new ChainFlatteningRewriter(searchParamTableExpressionQueryGeneratorFactory);
            var sortRewriter = new SortRewriter(searchParamTableExpressionQueryGeneratorFactory);
            var partitionEliminationRewriter = new PartitionEliminationRewriter(sqlServerFhirModel, SchemaInformation, () => searchableSearchParameterDefinitionManager);
            var compartmentDefinitionManager = new CompartmentDefinitionManager(ModelInfoProvider.Instance);
            compartmentDefinitionManager.StartAsync(CancellationToken.None).Wait();
            var compartmentSearchRewriter = new SqlCompartmentSearchRewriter(new Lazy<ICompartmentDefinitionManager>(() => compartmentDefinitionManager), new Lazy<ISearchParameterDefinitionManager>(() => _searchParameterDefinitionManager));
            var smartCompartmentSearchRewriter = new SmartCompartmentSearchRewriter(compartmentSearchRewriter, new Lazy<ISearchParameterDefinitionManager>(() => _searchParameterDefinitionManager), Options.Create(new CoreFeatureConfiguration()));

            _fhirSqlConfiguration = new FhirSqlServerConfiguration();
            var queryPlanReuseChecker = new QueryPlanReuseChecker(SqlRetryService, _fhirSqlConfiguration, NullLogger<QueryPlanReuseChecker>.Instance);

            SqlQueryHashCalculator = new TestSqlHashCalculator();

            // The shared service keeps a substituted router (baseline behaviour: every search stays on the
            // legacy path), so the existing suite is unchanged.
            var ignixaSqlCompileOnlyRouter = Substitute.For<IIgnixaSqlCompileOnlyRouter>();

            _searchService = new SqlServerSearchService(
                searchOptionsFactory,
                _fhirDataStore,
                sqlServerFhirModel,
                sqlRootExpressionRewriter,
                chainFlatteningRewriter,
                sortRewriter,
                partitionEliminationRewriter,
                compartmentSearchRewriter,
                smartCompartmentSearchRewriter,
                searchParamTableExpressionQueryGeneratorFactory,
                SqlRetryService,
                SqlServerDataStoreConfiguration,
                _fhirSqlConfiguration,
                SchemaInformation,
                _fhirRequestContextAccessor,
                new CompressedRawResourceConverter(),
                SqlQueryHashCalculator,
                queryPlanReuseChecker,
                ignixaSqlCompileOnlyRouter,
                NullLogger<SqlServerSearchService>.Instance);

            // Parallel search service wired for real Ignixa execution. It reuses the same rewriters, model,
            // schema and data store as the shared service, but pairs a second SearchOptionsFactory (using the
            // real Ignixa adapter, so SearchOptions.IgnixaOptions is populated) with a real compile-only router
            // configured to execute the emitted SQL. Eligible searches run through this service actually execute
            // Ignixa-generated SQL against the live database.
            var ignixaExecutionConfiguration = new FhirSqlServerConfiguration();
            ignixaExecutionConfiguration.EnableIgnixaSqlExecution = true;

            var ignixaSearchOptionsFactory = new SearchOptionsFactory(
                expressionParser,
                () => searchableSearchParameterDefinitionManager,
                _options,
                _fhirRequestContextAccessor,
                sqlSortingValidator,
                new ExpressionAccessControl(_fhirRequestContextAccessor),
                CreateRealIgnixaSearchOptionsAdapter(),
                new IgnixaSearchTenantAccessor(_fhirRequestContextAccessor),
                NullLogger<SearchOptionsFactory>.Instance);

            var ignixaExecutionRouter = new IgnixaSqlCompileOnlyRouter(
                new IgnixaSqlCompilerAdapter(
                    new IgnixaSqlSymbolResolver(sqlServerFhirModel),
                    SchemaInformation,
                    new global::Ignixa.Search.Definition.CompartmentDefinitionManager(IgnixaFhirVersionAdapter.Current),
                    CreateIgnixaSearchParameterDefinitionManager(),
                    new CollectingLogger<IgnixaSqlCompilerAdapter>(IgnixaRouterLog)),
                ignixaExecutionConfiguration,
                new CollectingLogger<IgnixaSqlCompileOnlyRouter>(IgnixaRouterLog));

            IgnixaSearchService = new SqlServerSearchService(
                ignixaSearchOptionsFactory,
                _fhirDataStore,
                sqlServerFhirModel,
                sqlRootExpressionRewriter,
                chainFlatteningRewriter,
                sortRewriter,
                partitionEliminationRewriter,
                compartmentSearchRewriter,
                smartCompartmentSearchRewriter,
                searchParamTableExpressionQueryGeneratorFactory,
                SqlRetryService,
                SqlServerDataStoreConfiguration,
                ignixaExecutionConfiguration,
                SchemaInformation,
                _fhirRequestContextAccessor,
                new CompressedRawResourceConverter(),
                SqlQueryHashCalculator,
                queryPlanReuseChecker,
                ignixaExecutionRouter,
                NullLogger<SqlServerSearchService>.Instance);

            ISearchParameterSupportResolver searchParameterSupportResolver = Substitute.For<ISearchParameterSupportResolver>();
            searchParameterSupportResolver.IsSearchParameterSupported(Arg.Any<SearchParameterInfo>()).Returns((true, false));

            _searchParameterStatusManager = new SearchParameterStatusManager(
                SqlServerSearchParameterStatusDataStore,
                _searchParameterDefinitionManager,
                searchParameterSupportResolver,
                _mediator,
                NullLogger<SearchParameterStatusManager>.Instance);

            _sqlQueueClient = new SqlQueueClient(SchemaInformation, SqlRetryService, NullLogger<SqlQueueClient>.Instance);

            await _searchParameterDefinitionManager.EnsureInitializedAsync(CancellationToken.None);
            await _searchParameterStatusManager.EnsureInitializedAsync(CancellationToken.None);
        }

        public async Task DisposeAsync()
        {
            await _testHelper.DeleteDatabase(_databaseName, CancellationToken.None);
        }

        protected SqlConnection GetSqlConnection(string connectionString)
        {
            var connectionBuilder = new SqlConnectionStringBuilder(connectionString);
            var result = new SqlConnection(connectionBuilder.ToString());
            return result;
        }

        private static global::Ignixa.Search.Definition.ISearchParameterDefinitionManager CreateIgnixaSearchParameterDefinitionManager()
        {
            global::Ignixa.Abstractions.IFhirSchemaProvider schemaProvider = IgnixaFhirVersionAdapter.Current.GetSchemaProvider();
            var searchParameterDefinitionManager = new global::Ignixa.Search.Definition.SearchParameterDefinitionManager(
                schemaProvider,
                NullLogger<global::Ignixa.Search.Definition.SearchParameterDefinitionManager>.Instance);

            return new global::Ignixa.Search.Definition.SearchableSearchParameterDefinitionManager(searchParameterDefinitionManager);
        }

        private static IIgnixaSearchOptionsAdapter CreateRealIgnixaSearchOptionsAdapter()
        {
            global::Ignixa.Abstractions.IFhirSchemaProvider schemaProvider = IgnixaFhirVersionAdapter.Current.GetSchemaProvider();

            var baseUriProvider = Substitute.For<global::Ignixa.Abstractions.IFhirBaseUriProvider>();
            baseUriProvider.GetServiceBaseUris().Returns(Array.Empty<Uri>());
            var referenceSearchValueParser = new global::Ignixa.Search.Indexing.SearchValues.ReferenceSearchValueParser(schemaProvider, baseUriProvider);
            var searchParameterExpressionParser = new global::Ignixa.Search.Expressions.Parsers.SearchParameterExpressionParser(referenceSearchValueParser, schemaProvider);
            var searchParameterDefinitionManager = new global::Ignixa.Search.Definition.SearchParameterDefinitionManager(
                schemaProvider,
                NullLogger<global::Ignixa.Search.Definition.SearchParameterDefinitionManager>.Instance);
            var searchableSearchParameterDefinitionManager = new global::Ignixa.Search.Definition.SearchableSearchParameterDefinitionManager(searchParameterDefinitionManager);
            global::Ignixa.Search.Definition.ISearchParameterDefinitionManager.SearchableSearchParameterDefinitionManagerResolver resolver = () => searchableSearchParameterDefinitionManager;
            var expressionParser = new global::Ignixa.Search.Expressions.Parsers.ExpressionParser(
                resolver,
                searchParameterExpressionParser,
                schemaProvider);
            var builderFactory = new IgnixaSearchOptionsBuilderFactory(expressionParser, searchableSearchParameterDefinitionManager);

            return new IgnixaSearchOptionsAdapter(builderFactory, schemaProvider);
        }

        object IServiceProvider.GetService(Type serviceType)
        {
            if (serviceType == typeof(IFhirDataStore))
            {
                return _fhirDataStore;
            }

            if (serviceType == typeof(IFhirOperationDataStore))
            {
                return _fhirOperationDataStore;
            }

            if (serviceType == typeof(SqlServerFhirOperationDataStore))
            {
                return _sqlServerFhirOperationDataStore;
            }

            if (serviceType == typeof(IFhirStorageTestHelper))
            {
                return _testHelper;
            }

            if (serviceType == typeof(ISqlServerFhirStorageTestHelper))
            {
                return _testHelper;
            }

            if (serviceType == typeof(ITransactionHandler))
            {
                return SqlTransactionHandler;
            }

            if (serviceType == typeof(ISearchParameterStatusDataStore))
            {
                return SqlServerSearchParameterStatusDataStore;
            }

            if (serviceType == typeof(FilebasedSearchParameterStatusDataStore))
            {
                return _filebasedSearchParameterStatusDataStore;
            }

            if (serviceType == typeof(ISearchService))
            {
                return _searchService;
            }

            if (serviceType == typeof(SearchParameterDefinitionManager))
            {
                return _searchParameterDefinitionManager;
            }

            if (serviceType == typeof(SupportedSearchParameterDefinitionManager))
            {
                return _supportedSearchParameterDefinitionManager;
            }

            if (serviceType == typeof(SchemaUpgradeRunner))
            {
                return _schemaUpgradeRunner;
            }

            if (serviceType == typeof(SearchParameterStatusManager))
            {
                return _searchParameterStatusManager;
            }

            if (serviceType == typeof(RequestContextAccessor<IFhirRequestContext>))
            {
                return _fhirRequestContextAccessor;
            }

            if (serviceType == typeof(IQueueClient))
            {
                return _sqlQueueClient;
            }

            if (serviceType == typeof(TestSqlHashCalculator))
            {
                return SqlQueryHashCalculator as TestSqlHashCalculator;
            }

            if (serviceType == typeof(FhirSqlServerConfiguration))
            {
                return _fhirSqlConfiguration;
            }

            return null;
        }

        /// <summary>
        /// An <see cref="ILogger{TCategoryName}"/> that appends every formatted message to a shared queue so a
        /// differential test can read the Ignixa router's fallback reasons. The router deliberately logs one
        /// distinct reason string per gate, which makes an unexpected legacy fallback self-diagnosing.
        /// </summary>
        private sealed class CollectingLogger<T> : ILogger<T>
        {
            private readonly ConcurrentQueue<string> _sink;

            public CollectingLogger(ConcurrentQueue<string> sink)
            {
                _sink = sink;
            }

            public IDisposable BeginScope<TState>(TState state)
                where TState : notnull => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                if (formatter != null)
                {
                    _sink.Enqueue($"{typeof(T).Name}: {formatter(state, exception)}");
                }
            }

            private sealed class NullScope : IDisposable
            {
                internal static readonly NullScope Instance = new NullScope();

                public void Dispose()
                {
                }
            }
        }
    }
}
