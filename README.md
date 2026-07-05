# Video Streaming API

ASP.NET Core service for course-style video uploads: resumable TUS ingest, FFmpeg HLS transcoding, and MinIO object storage.

**Interactive docs:** `/swagger`

## Stack

| Layer | Tech |
|-------|------|
| API | ASP.NET Core 10 |
| Database | PostgreSQL, EF Core |
| Queue | Hangfire (in-memory) |
| Uploads | tus.io |
| Transcode | FFmpeg (adaptive HLS) |
| Storage | MinIO |

## Endpoints

| Method | Path | Purpose |
|--------|------|---------|
| GET | `/api/video` | List videos |
| GET | `/api/video/{id}` | Video metadata |
| GET | `/api/video/{id}/status` | Processing status |
| GET | `/api/video/{id}/progress` | SSE transcode progress |
| POST | `/api/video/upload` | Single-shot upload (testing) |
| POST | `/api/files` | TUS resumable upload |

## Run locally

```bash
cp .env.example .env   # set PostgreSQL + MinIO
dotnet run             # http://localhost:5213/swagger
```

## Docker

```bash
docker build -t video-streaming-api .
docker run -p 8080:8080 --env-file .env video-streaming-api
```

PostgreSQL and MinIO must be reachable from the container (see `.env.example`).

## Configuration

Copy `.env.example` to `.env` locally — **never commit `.env`**. Credentials are read from environment variables only; `appsettings.json` contains no secrets.
