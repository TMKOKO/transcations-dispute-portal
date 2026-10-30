# Stage 1: Build the static bundle
FROM node:20-alpine AS build
WORKDIR /app

COPY package.json package-lock.json ./
RUN npm ci --no-audit --no-fund

COPY . .
# Vite bakes these in at build time. VITE_DEMO_HINT shows the demo credentials on the login page —
# leave it false for anything but a local demo.
ARG VITE_API_BASE=http://localhost:8080
ARG VITE_DEMO_HINT=false
ARG VITE_KAFKA_UI_URL=
ENV VITE_API_BASE=$VITE_API_BASE VITE_DEMO_HINT=$VITE_DEMO_HINT VITE_KAFKA_UI_URL=$VITE_KAFKA_UI_URL
RUN npm run build

# Stage 2: Serve with nginx as a non-root user (listens on 8080)
FROM nginxinc/nginx-unprivileged:1.27-alpine AS runtime

# The CSP's connect-src must name the API origin; the official image runs envsubst over
# /etc/nginx/templates/*.template at start-up, so it is set per environment, not per build.
ENV API_ORIGIN=http://localhost:8080
COPY nginx.conf.template /etc/nginx/templates/default.conf.template
COPY --from=build /app/dist /usr/share/nginx/html

EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=3s --retries=3 CMD wget -qO- http://127.0.0.1:8080/healthz || exit 1
