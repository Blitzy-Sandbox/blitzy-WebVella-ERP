<!--{"sort_order":1, "name": "overview", "label": "Overview"}-->
# Overview

The server API is implemented by the following classes

**Note:** These are **in-process C# managers** — **EntityManager**, **EntityRelationManager**, **RecordManager**, and **SecurityManager** — that are **unchanged** by the headless refactor and continue to run **in-process**. Use them directly from plugin or server-side code.

> **Planned (headless refactor — not yet implemented).** A `/api/v1/` REST surface that wraps these managers for remote/HTTP consumers is planned but does not exist in the current checkout; when built it is intended to delegate to these same managers rather than replace them. See the planned [API Reference](../../api-reference/index.md).

Source: /WebVella.Erp/Api/EntityManager.cs, /WebVella.Erp/Api/EntityRelationManager.cs, /WebVella.Erp/Api/RecordManager.cs, and /WebVella.Erp/Api/SecurityManager.cs implement these in-process managers.

## EntityManager

Entity meta and entity field related operations. **Important:** Requires `Administration` role

```csharp
Entity entity = new EntityManager().ReadEntity("user").Object;
```

## EntityRelationManager

Entity relations operation. **Important:** Requires `Administration` role

```csharp
List<EntityRelation> relationList = new EntityRelationManager().Read(storageEntityList).Object;
```

## RecordManager

Operations with entity records. **Important:** The access depends on the preferences selected in the corresponding entity

```csharp
var createResponse = new RecordManager().CreateRecord("offer", PostObject);
```

## SecurityManager

```csharp
var user = new SecurityManager().GetUser(userId);
```

User and User role related operations