# FHIR Fanout Broker Query Service

A read-only FHIR service that aggregates search queries across multiple FHIR servers, implementing ADR 2506 for multi-server FHIR search aggregation.

## Overview

The FHIR Fanout Broker Query Service acts as a FHIR-compliant proxy that intelligently distributes search queries across multiple configured FHIR servers and aggregates the results into unified responses. This enables healthcare organizations to query data across distributed FHIR servers through a single endpoint.

## Key Features

### Core Functionality
- **Read-only search operations** at system level (`GET /?[search]`) and resource type level (`GET /[ResourceType]/?[search]`)
- **Rejects point reads and write operations** with appropriate HTTP status codes (405/501)
- **Adaptive execution strategy** - parallel for targeted queries, sequential for broad searches
- **FHIR R4 compliance** with proper Bundle responses and continuation tokens

### Advanced Search Support
- **Distributed sorting** with global merge algorithm and continuation token management
- **Chained search expressions** with timeout protection and payload optimization
- **Include/RevInclude operations** (planned for Phase 3)
- **Standard search parameters**: `_count`, `_sort`, `_elements`, `_id`, `_lastModified`

#### Chained Search Implementation
The fanout broker supports both forward and reverse chained searches across multiple FHIR servers:

**Forward Chained Searches** (e.g., `Observation?subject.name=John`):
- Executes sub-queries on target resource servers (Patient) to find matching resources
- Uses `_elements=id` optimization to minimize payload transfer
- Converts results to ID filters for the main query on source resources (Observation)

**Reverse Chained Searches** (e.g., `Patient?_has:Group:member:_id=group123`):
- Searches source resource servers (Group) for resources matching the criteria
- Extracts reference information to identify target resources (Patient)
- Applies timeout protection (configurable via `ChainSearchTimeoutSeconds`)

**Timeout Protection**:
- Configurable timeout for chained search operations (default: 15 seconds)
- Automatic fallback to `RequestTooCostlyException` if timeout exceeded
- Prevents runaway queries from impacting service performance

### Architecture Components
- **ExecutionStrategyAnalyzer** - Determines optimal query execution approach
- **FhirServerOrchestrator** - Manages communication with target FHIR servers
- **ResultAggregator** - Merges results from multiple servers with intelligent deduplication
- **Circuit Breaker** - Provides fault tolerance for unreliable servers
- **Health Monitoring** - Tracks availability and performance of target servers

## Configuration

Configure target FHIR servers in `appsettings.json`:

```json
{
  "FanoutBroker": {
    "FhirServers": [
      {
        "Id": "server1",
        "Name": "Primary FHIR Server",
        "BaseUrl": "https://server1.example.com/fhir",
        "IsEnabled": true,
        "Priority": 1,
        "Authentication": {
          "Type": "Bearer",
          "BearerToken": "your-bearer-token"
        }
      },
      {
        "Id": "server2",
        "Name": "Secondary FHIR Server", 
        "BaseUrl": "https://server2.example.com/fhir",
        "IsEnabled": true,
        "Priority": 2,
        "Authentication": {
          "Type": "ClientCredentials",
          "ClientId": "client-id",
          "ClientSecret": "client-secret",
          "TokenEndpoint": "https://auth.example.com/oauth2/token"
        }
      }
    ],
    "SearchTimeoutSeconds": 30,
    "ChainSearchTimeoutSeconds": 15,
    "MaxChainDepth": 3,
    "FillFactor": 0.5,
    "MaxResultsPerServer": 1000,
    "EnableCircuitBreaker": true,
    "CircuitBreakerFailureThreshold": 5
  }
}
```

### Configuration Options

- `SearchTimeoutSeconds` - Global timeout for search operations (default: 30)
- `ChainSearchTimeoutSeconds` - Timeout for chained search sub-queries (default: 15)
- `MaxChainDepth` - Maximum allowed depth for nested chained expressions (default: 3)
- `FillFactor` - Fill factor threshold for sequential execution (default: 0.5)
- `MaxResultsPerServer` - Maximum results from a single server (default: 1000)
- `EnableCircuitBreaker` - Enable circuit breaker pattern (default: true)
```

### Authentication Types

- **None** - No authentication
- **Bearer** - Bearer token authentication
- **Basic** - Basic authentication with username/password
- **ClientCredentials** - OAuth2 client credentials flow (placeholder)

## Execution Strategies

The service automatically determines the optimal execution strategy based on query analysis:

### Parallel Execution (Default)
**Used for:**
- Exact ID searches (`_id=123`)
- Specific identifier searches (`identifier=system|value`)
- Queries with sort parameters (requires global sorting)
- Small result sets (`_count≤10`)
- Chained searches (comprehensive collection needed)

### Sequential Execution
**Used for:**
- Broad text searches (`name=John`)
- Status-based searches (`status=active`)
- Large count values (`_count>20`)
- Queries likely to return >500 results per server

## API Endpoints

### Search Operations (Supported)
- `GET /?[search]` - System-level search across all resource types
- `GET /Patient?[search]` - Resource-specific search
- `GET /metadata` - Capability statement (intersection of all servers)

### Rejected Operations
- `GET /Patient/123` - Point reads → 501 Not Implemented
- `POST /Patient` - Create operations → 405 Method Not Allowed  
- `PUT /Patient/123` - Update operations → 405 Method Not Allowed
- `DELETE /Patient/123` - Delete operations → 405 Method Not Allowed

### Health Monitoring
- `GET /health` - Service health with server status details

## Response Format

Responses follow standard FHIR Bundle format with enhanced `fullUrl` values for server traceability:

```json
{
  "resourceType": "Bundle",
  "type": "searchset",
  "entry": [
    {
      "fullUrl": "https://server1.example.com/fhir/Patient/123",
      "resource": { "resourceType": "Patient", "id": "123" },
      "search": { "mode": "match" }
    },
    {
      "fullUrl": "https://server2.example.com/fhir/Patient/456", 
      "resource": { "resourceType": "Patient", "id": "456" },
      "search": { "mode": "match" }
    }
  ],
  "link": [
    {
      "relation": "next",
      "url": "?_count=50&ct=eyJzZXJ2ZXJzIjpb..."
    }
  ]
}
```

### Chained Search Examples

The fanout broker supports FHIR chained search expressions across multiple servers:

**Forward Chained Search** - Find observations for patients named "John":
```bash
GET /Observation?subject.name=John
```

**Reverse Chained Search** - Find patients who are members of a specific group:
```bash
GET /Patient?_has:Group:member:_id=group123
```

**Complex Chained Search** - Find observations for patients with specific identifier:
```bash
GET /Observation?subject.identifier=http://hospital.org/mrn|123456
```

The service automatically:
1. Detects chained parameters in the query
2. Executes optimized sub-queries on target servers using `_elements=id`
3. Converts results to ID filters for the main query
4. Applies timeout protection to prevent runaway queries

## Running the Service

```bash
# Development
dotnet run --project src/Microsoft.Health.Fhir.R4.FanoutBroker

# Production
dotnet publish -c Release
dotnet Microsoft.Health.Fhir.R4.FanoutBroker.dll
```

The service will be available at:
- API: `http://localhost:5000`
- Swagger UI: `http://localhost:5000` (development only)
- Health checks: `http://localhost:5000/health`

## Implementation Status

### ✅ Phase 1 - Foundation
- Project structure and core interfaces
- Execution strategy analysis
- Configuration models
- Basic controller and capability provider

### ✅ Phase 2 - Core Logic  
- Server orchestration with authentication
- Circuit breaker pattern
- Result aggregation with sorting
- Health monitoring
- Complete service setup

### ✅ Phase 3 - Advanced Features (Partial)
- ✅ **Chained search expressions** with timeout protection and payload optimization
- ✅ Forward chained searches (e.g., `Observation?subject.name=John`) 
- ✅ Reverse chained searches (e.g., `Patient?_has:Group:member:_id=group123`)
- ✅ Configurable timeout protection for complex chained queries
- ✅ `_elements=id` optimization for minimal payload transfer
- 📋 Include/RevInclude processing (planned)
- 📋 Advanced continuation token handling for chained queries (planned)
- 📋 Performance optimizations for complex search scenarios (planned)

### 📋 Phase 4 - Production Ready (Planned)
- Comprehensive testing
- Performance monitoring
- Configuration validation
- Deployment documentation

## Architecture Decision Record

This implementation follows [ADR 2506: Fanout Broker Query Service for Multi-Server FHIR Search Aggregation](https://github.com/microsoft/fhir-server/issues/2506), providing a read-only FHIR proxy that enables unified access to distributed healthcare data while maintaining FHIR specification compliance.

## Contributing

This service is part of the Microsoft FHIR Server project. Please follow the project's contribution guidelines and coding standards.