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
- **Include/RevInclude operations** (planned for Phase 3)
- **Chained search expressions** (planned for Phase 3)
- **Standard search parameters**: `_count`, `_sort`, `_elements`, `_id`, `_lastModified`

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
    "FillFactor": 0.5,
    "EnableCircuitBreaker": true,
    "CircuitBreakerFailureThreshold": 5
  }
}
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

### 🔄 Phase 3 - Advanced Features (Planned)
- Include/RevInclude processing
- Chained search expressions
- Advanced continuation token handling
- Performance optimizations

### 📋 Phase 4 - Production Ready (Planned)
- Comprehensive testing
- Performance monitoring
- Configuration validation
- Deployment documentation

## Architecture Decision Record

This implementation follows [ADR 2506: Fanout Broker Query Service for Multi-Server FHIR Search Aggregation](https://github.com/microsoft/fhir-server/issues/2506), providing a read-only FHIR proxy that enables unified access to distributed healthcare data while maintaining FHIR specification compliance.

## Contributing

This service is part of the Microsoft FHIR Server project. Please follow the project's contribution guidelines and coding standards.