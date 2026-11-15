# ---- Stage 1: frontend build ---------------------------------------------
FROM node:20-alpine AS frontend
WORKDIR /app

# Copy manifests first for a reusable install layer
COPY Frontend/package*.json ./
RUN npm ci

# Copy source and build the production bundle -> dist/
COPY Frontend/ ./
RUN npm run build

# ---- Stage 2: API build ----------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project file first for a reusable restore layer
COPY WebApi/WebApi.csproj WebApi/
RUN dotnet restore WebApi/WebApi.csproj

# Copy source and publish (Release)
COPY WebApi/ WebApi/
# ST-127: the release version is passed in (the release tag, e.g. 0.5.0); the csproj default
# covers local builds. The git build context is retired with the commit-count derivation.
ARG APP_VERSION=0.5.0
RUN dotnet publish WebApi/WebApi.csproj -c Release -o /app/publish /p:UseAppHost=false /p:ReleaseVersion=${APP_VERSION}

# ---- Stage 3: runtime ------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine AS final
WORKDIR /app

COPY --from=build /app/publish .
COPY --from=frontend /app/dist ./wwwroot
# ST-14: the chord-page→ChordPro system prompt stays a repo file (single source of truth)
COPY docs/prompts/chord-page-to-chordpro.md ./prompts/chord-page-to-chordpro.md
# ST-35 F3: the metadata review prompt, same pattern.
COPY docs/prompts/metadata-review.md ./prompts/metadata-review.md
# ST-31: the changelog stays a repo file; /api/info serves it to Settings → About
COPY CHANGELOG.md ./changelog.md

ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000

ENTRYPOINT ["dotnet", "WebApi.dll"]
