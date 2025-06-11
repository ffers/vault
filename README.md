# Vault with Nginx

This repository provides a simple `docker-compose.yml` that runs
[HashiCorp Vault](https://www.vaultproject.io/) together with an
[Nginx](https://nginx.org/) reverse proxy.

## Usage

1. Make sure you have Docker and Docker Compose installed.
2. Run `docker compose up -d` to start Vault and Nginx.
3. Access Vault through `http://localhost` (proxied by Nginx). The Vault
   UI is available at `http://localhost/ui`.

The Vault container runs in development mode with the root token set to
`root`. Do **not** use this setup for production.
