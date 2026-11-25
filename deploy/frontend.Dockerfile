FROM node:22-alpine AS build
WORKDIR /src
COPY ["FrontEnd for SmartFleet/package.json", "FrontEnd for SmartFleet/package-lock.json", "./"]
RUN npm ci
COPY ["FrontEnd for SmartFleet/", "./"]
RUN npm run build
FROM nginx:alpine
COPY deploy/nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /src/dist /usr/share/nginx/html
EXPOSE 80
