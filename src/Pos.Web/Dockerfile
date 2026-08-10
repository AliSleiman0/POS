# The web app: built to static assets, served by Caddy.
#
# Build from THIS directory:
#
#   docker build -t pos-web --build-arg VITE_API_BASE_URL=https://pos-api.fly.dev src/Pos.Web
#
# VITE_API_BASE_URL is a build argument rather than a runtime environment variable
# because Vite inlines VITE_* into the bundle at build time. There is no way to
# change it in a running container: pointing the app at a different API means
# rebuilding and redeploying it. For the same reason nothing secret may ever be
# passed here — it would end up in JavaScript the browser downloads.

# ---------------------------------------------------------------- build stage
FROM node:24-alpine AS build
WORKDIR /src

# Corepack ships with Node and pins pnpm to the version in package.json's
# packageManager field, so the image cannot quietly resolve a different one than
# a developer or CI does.
RUN corepack enable

# Manifests first, so a source edit reuses the installed dependency layer.
COPY package.json pnpm-lock.yaml ./

# --frozen-lockfile fails if package.json and the lockfile disagree, rather than
# silently resolving a different tree inside the image than the one that was tested.
RUN pnpm install --frozen-lockfile

COPY . .

ARG VITE_API_BASE_URL
ENV VITE_API_BASE_URL=$VITE_API_BASE_URL

# `build` runs `tsc -b` first, so a type error fails here rather than shipping.
RUN pnpm build

# -------------------------------------------------------------- runtime stage
FROM caddy:2-alpine AS runtime

COPY --from=build /src/dist /srv
COPY Caddyfile /etc/caddy/Caddyfile

EXPOSE 8080
