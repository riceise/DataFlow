FROM node:24.15.0-alpine AS build
WORKDIR /app
COPY Ui/package.json Ui/package-lock.json ./
RUN npm ci
COPY Ui/ ./
RUN npm run build
FROM nginxinc/nginx-unprivileged:1.28.0-alpine
COPY deploy/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=5s CMD wget -q -O /dev/null http://127.0.0.1:8080/health/live || exit 1
