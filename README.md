# Tron USDT Deposit Monitor

The Tron USDT Deposit Monitor is a C# application that monitors specified Tron addresses for incoming USDT transactions and sends notifications to designated WhatsApp groups using the Green API.

## Features

- Monitors multiple Tron addresses for incoming USDT transactions
- Supports configuring multiple WhatsApp groups for notifications
- Sends formatted transaction details to WhatsApp groups
- Implements rate limiting to stay within API usage limits
- Dockerized for easy deployment and management

## Configuration

1. Create a `.env` file with the following variables:
ID_INSTANCE=your_id_instance API_TOKEN_INSTANCE=your_api_token_instance SENDER_NUMBER=your_sender_number


2. Create an `address-groups.json` file to define the WhatsApp groups and associated Tron addresses:

```json
{
  "group_id_1": [
    "address_1",
    "address_2"
  ],
  "group_id_2": [
    "address_3",
    "address_4"
  ]
}
```

## Deployment
Install Docker and Docker Compose.
Place the .env and address-groups.json files in the project root.
Build and run the Docker container:

```bash

docker-compose up -d

```
Monitoring and Alerting
Set up monitoring to receive alerts if the application stops working. Options:

Docker monitoring solution like Prometheus and Grafana
Health checks and alerts in container orchestration platform
Custom monitoring and alerting within the application

##  Troubleshooting

Common issues and resolutions:

1. API rate limiting errors: Adjust rate limiting settings or contact API provider.

2. Connectivity problems: Check network settings, firewall rules, and API endpoint availability.