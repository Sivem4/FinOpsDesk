FROM node:24.18.0@sha256:5711a0d445a1af54af9589066c646df387d1831a608226f4cd694fc59e745059
WORKDIR /src/frontend
COPY src/frontend/package.json src/frontend/package-lock.json ./
RUN npm ci
COPY src/frontend/ ./
EXPOSE 5173
CMD ["npm", "run", "dev", "--", "--host", "0.0.0.0", "--port", "5173", "--strictPort"]
