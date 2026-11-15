# Contributing

Small fixes, docs, features — welcome.

## Dev loop

Prereqs: .NET 10 SDK, Node ≥ 22, Docker.

```bash
docker compose up -d postgres   # :5432
cd WebApi && dotnet run         # API on :5000
cd Frontend && npm run dev      # UI on :5173, proxies /api
```

## AI assistance

You can use AI tools for your work on this repo. If you feel like a substantial part of
your code was written by AI, please state it in the PR, and please make sure you are in
control of what is being written. (Please no vibe coding.)
