# Video Streaming API

ASP.NET Core service for course-style video uploads: resumable TUS ingest, FFmpeg HLS transcoding, and MinIO object storage.

**Interactive docs:** `/swagger` (upload limits, TUS, and all endpoints)

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
| GET | `/api/video/upload-limits` | Maximum upload size (documented in Swagger) |
| GET | `/api/media/{path}` | HLS segments and thumbnails (playback) |
| POST | `/api/video/upload` | Single-shot upload (testing; see Swagger) |
| POST | `/api/files` | TUS resumable upload (documented in Swagger) |

## Frontend integration

Brief guide for any web client (React, Next.js, Vue, etc.):

1. **API base URL** — Configure one origin the browser can reach (e.g. `NEXT_PUBLIC_API_URL=https://your-api.example.com`). Call endpoints as `{base}/api/video`, `{base}/api/files`, etc.
2. **CORS** — Add your frontend origin to `Cors:AllowedOrigins` on the API.
3. **Upload** — Use [tus-js-client](https://github.com/tus/tus-js-client) on `{base}/api/files` with metadata `title` and `filename`.
4. **Transcode progress** — `EventSource` on `GET /api/video/{id}/progress` until status is `Ready` or `Failed`.
5. **Playback** — When ready, play the `hlsUrl` from the video response with [hls.js](https://github.com/video-dev/hls.js/) (URLs are under `/api/media/...`).
6. **Upload limits** — `GET /api/video/upload-limits` for max size; validate in the UI before starting tus.

See **Swagger** (`/swagger`) for request/response shapes and upload limits.

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

Upload size (`VideoProcessing:MaxUploadSizeGb`) is documented in **Swagger** at `/swagger`.
