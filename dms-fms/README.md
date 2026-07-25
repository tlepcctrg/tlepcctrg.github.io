# DMS / FMS — Distributed Storage Ecosystem (Reference Implementation)

A working .NET 10 (C#), API-only implementation of the two-tier
**Document Management Service (DMS)** / **File Management Service (FMS)**
architecture: a high-throughput, direct-to-storage document/folder
management platform backed by MongoDB, Redis, and Kafka.

There is no web/UI project here — both services expose APIs only (DMS: REST,
FMS: internal gRPC).

## Services

| Service     | Role                                                                                  | Protocol exposed        |
|-------------|----------------------------------------------------------------------------------------|--------------------------|
| `DMS.Api`   | Domain/metadata controller: folders, files, ACL/permission engine, temp-zone bookkeeping, event publishing | REST (public)            |
| `FMS.Api`   | Physical storage abstraction: pre-signed URLs, multipart uploads, object lifecycle       | gRPC (internal, DMS-only)|
| `FMS.Contracts` | Shared protobuf contracts consumed by both services                                | n/a (library)            |

```
Client ──REST──▶ DMS.Api ──gRPC (control-plane only)──▶ FMS.Api ──▶ S3/MinIO
   │                                                                    ▲
   └───────────────────── direct PUT/GET of file bytes ────────────────┘
```

Binary payloads never flow through DMS or FMS compute — only pre-signed URLs
are exchanged; the client uploads/downloads directly against object storage.

## Tech stack

- **.NET 10** / ASP.NET Core (Web API + gRPC)
- **MongoDB** — folder/file metadata using a hybrid **materialized path +
  ancestry array** hierarchy model
- **Redis** — L2 cache for effective-permission resolution
- **Kafka** — domain event bus (`FileUploaded`, `FileCommitted`,
  `FileDeleted`, `FolderMoved`, `PermissionChanged`)
- **MinIO** (S3-compatible) — object storage target for local development;
  swap `FMS.Api`'s `Storage:ServiceUrl` config to point at real AWS S3 with
  no code changes

## Running locally

1. Start infrastructure:

   ```bash
   docker compose up -d
   ```

   This brings up MongoDB, Redis, Kafka (KRaft mode, no ZooKeeper), and MinIO
   with the `dms-fms-temp` / `dms-fms-permanent` buckets pre-created.

2. Run FMS (gRPC, internal):

   ```bash
   dotnet run --project src/FMS.Api
   ```

3. Run DMS (REST, public) in another terminal:

   ```bash
   dotnet run --project src/DMS.Api
   ```

4. Build everything at once:

   ```bash
   dotnet build
   ```

## API walkthrough (direct-to-storage upload)

```bash
# 1. Create a root folder
curl -X POST http://localhost:5199/api/folders \
  -H "Content-Type: application/json" \
  -d '{"tenantId":"t1","parentId":null,"name":"root","ownerId":"user-1"}'

# 2. Init upload -> returns a pre-signed PUT URL, DMS never sees the bytes
curl -X POST http://localhost:5199/api/files/init-upload \
  -H "Content-Type: application/json" \
  -d '{"tenantId":"t1","folderId":"<folderId>","fileName":"report.pdf","contentType":"application/pdf","expectedSize":1024,"multipart":false,"partCount":0,"ownerId":"user-1"}'

# 3. Client uploads bytes directly to the returned presignedPutUrl (PUT), bypassing DMS/FMS entirely.

# 4. Commit once the upload completes
curl -X POST http://localhost:5199/api/files/<fileId>/commit \
  -H "Content-Type: application/json" \
  -d '{"tenantId":"t1","expectedSha256":"<sha256-of-uploaded-bytes>"}'

# 5. Download
curl "http://localhost:5199/api/files/<fileId>/download-url?tenantId=t1"
```

## Hierarchy & permission model

- Each `FolderDocument` stores a materialized `Path` (fast prefix/subtree
  scans) **and** an `Ancestors` array (O(1) ancestor lookup) — see
  `Models/FolderDocument.cs`.
- ACL entries (`AclOverrideDocument`) store only **explicit** grants/denials,
  never a fully-materialized inherited set. Effective permission resolution
  (`Services/PermissionEngine.cs`) fetches a node's already-denormalized
  ancestor chain and issues a single `$in` query against the ACL collection —
  no recursive N+1 queries regardless of hierarchy depth. Results are cached
  in Redis (`Services/PermissionEngine.cs`) for sub-millisecond repeat checks.
- Bulk folder moves (`BackgroundServices/FolderMoveBackgroundService.cs`) are
  processed asynchronously in bounded pages using the materialized-path
  prefix index, via an in-process `System.Threading.Channels` queue — the API
  call returns immediately with `202 Accepted` instead of blocking on a
  synchronous full-subtree rewrite.
- Permission changes are O(1) writes (`Services/PermissionsService.cs`); no
  fan-out write is required since propagation is resolved at read time.

## Background processing

- `FolderMoveBackgroundService` — paged, atomic subtree hierarchy rewrites.
- `TempCleanupBackgroundService` — periodic sweep that reclaims expired,
  never-committed uploads from both MongoDB and FMS object storage
  (idempotent; complements the MinIO/S3 bucket lifecycle expiration rule).
- `KafkaEventConsumerService` — consumes the `dms.events` topic (audit /
  cache-invalidation fan-out demonstration).

## Project layout

```
dms-fms/
├── DmsFms.slnx
├── docker-compose.yml
└── src/
    ├── FMS.Contracts/   # shared storage.proto
    ├── FMS.Api/         # gRPC service: pre-signed URLs, multipart, S3/MinIO adapter
    └── DMS.Api/         # REST API: folders, files, permissions, events, background jobs
```
