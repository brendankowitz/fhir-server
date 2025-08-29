# FHIR Fanout Broker - Include/RevInclude Support

This document describes the Include/RevInclude fanout processing implemented for the FHIR Fanout Broker Query Service.

## Overview

The Include/RevInclude functionality allows the fanout broker to aggregate included resources (_include and _revinclude parameters) across multiple FHIR servers, following the $includes operation pattern documented in ADR-2503-Bundle-include-operation.md.

## Features

### 1. Include Parameter Detection
- Automatically detects `_include` and `_revinclude` parameters in search queries
- Case-insensitive parameter matching
- Supports multiple include parameters in a single query

### 2. Distributed Include Processing
- Executes include queries across all configured FHIR servers in parallel
- Aggregates included resources from all servers
- Deduplicates resources based on resource type and ID
- Maintains original search results while adding included resources

### 3. $includes Operation Support
- Implements the `{resourceType}/$includes` endpoint for paginated retrieval
- Follows ADR-2503 specification for handling large numbers of included resources
- Provides continuation token support for paging through included resources
- Returns only included resources (not the original search results)

### 4. Fanout-Specific Features
- **Cross-Server Aggregation**: Collects included resources from all enabled FHIR servers
- **Deduplication**: Removes duplicate resources that appear on multiple servers
- **Timeout Protection**: Uses configurable timeouts to prevent long-running include operations
- **Error Handling**: Gracefully handles server failures during include processing

## Usage Examples

### Basic Include Query
```bash
# Search patients and include their organizations across all servers
curl "http://localhost:5000/Patient?name=John&_include=Patient:organization"
```

### Multiple Include Parameters
```bash
# Search observations with multiple include parameters
curl "http://localhost:5000/Observation?code=8867-4&_include=Observation:patient&_include=Observation:performer"
```

### Reverse Include Query
```bash
# Find patients with observations that have a specific code
curl "http://localhost:5000/Patient?_revinclude=Observation:patient&_revinclude:Observation:code=8867-4"
```

### $includes Operation
```bash
# Retrieve additional included resources when the original search exceeds 1000 includes
curl "http://localhost:5000/Patient/$includes?_include=Patient:organization&includesCt=eyJzZXJ2ZXJzIjpbXX0="
```

## Configuration

The include processing behavior can be configured in `appsettings.json`:

```json
{
  "FanoutBroker": {
    "FhirServers": [
      {
        "Id": "server1",
        "BaseUrl": "https://server1.example.com/fhir",
        "IsEnabled": true
      }
    ],
    "SearchTimeoutSeconds": 30,
    "ChainSearchTimeoutSeconds": 15,
    "MaxChainDepth": 3,
    "FillFactor": 0.5
  }
}
```

## Implementation Details

### Components

1. **IIncludeProcessor**: Interface defining include processing operations
2. **IncludeProcessor**: Main implementation handling distributed include logic
3. **FanoutController**: Extended with `$includes` endpoint support
4. **FanoutSearchService**: Integrated include processing into main search flow

### Key Algorithms

1. **Parallel Include Fetching**: Executes include queries on all servers simultaneously
2. **Resource Deduplication**: Uses resource type + ID as unique key
3. **Result Merging**: Combines original results with included resources
4. **Continuation Token Management**: Handles pagination for $includes operation

### Error Handling

- Server failures during include processing don't fail the entire request
- Failed include operations are logged but don't prevent returning main results
- Timeout protection prevents runaway include queries
- Graceful degradation when include processing fails

## Compliance with ADR-2503

This implementation follows the Bundle Include Operation ADR:

- **Bundle Limit**: Respects 1000-item limit for included resources in main bundle
- **Related Link**: Provides link to $includes operation when limit exceeded
- **Pagination**: Supports continuation tokens for $includes operation
- **Original Link**: $includes response includes reference to original search
- **Performance**: Optimized queries using `_elements=id` when appropriate

## Testing

The implementation includes comprehensive unit tests covering:

- Include parameter detection
- Cross-server include processing
- Resource deduplication
- $includes operation functionality
- Error handling scenarios

Run tests with:
```bash
dotnet test src/Microsoft.Health.Fhir.R4.FanoutBroker.UnitTests/
```

## Limitations

1. **Simplified Implementation**: Uses basic include parameter parsing (full FHIR expression parsing not implemented)
2. **No Complex Chaining**: Advanced include chains are not fully supported
3. **Performance**: Large numbers of included resources may impact response time
4. **Server Dependency**: Requires all downstream servers to support include parameters

## Future Enhancements

- Advanced include expression parsing
- Intelligent include result filtering based on main search results
- Caching of frequently accessed included resources
- Support for include modifiers (`:iterate`, `:recurse`)
- Performance optimization for large include operations