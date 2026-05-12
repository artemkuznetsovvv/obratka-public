# Infra

## Dev: dependencies only (Postgres + Seq)

Backend and frontend run on host machine (`dotnet run` / `npm run dev`).
This compose file brings up only the dependencies they need.

```powershell
docker compose -f infra/docker-compose.dev.yml up -d
```

| Service     | Port  | Notes                                                        |
|-------------|-------|--------------------------------------------------------------|
| webapi-db   | 5432  | Postgres 16; db `obratka_webapi`, user `obratka` / `obratka_dev` |
| seq         | 5341  | Log UI at `http://localhost:5341`                            |

Stop:
```powershell
docker compose -f infra/docker-compose.dev.yml down
```

Wipe data (drops DB + Seq state):
```powershell
docker compose -f infra/docker-compose.dev.yml down -v
```

## Notes
- Default dev creds match `backend/src/Obratka.WebApi/appsettings.Development.json`.
- Seed admin user `admin@obratka.local` / `***REMOVED***` is created on first `dotnet run` (in `DbInitializer`).
