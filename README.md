# Integration-Hub-MSMQ-Bridge


## Prerequisites
Make sure you have the following installed and set up:
- [.NET Framework](https://dotnet.microsoft.com/download) version 8.0
- `az login --tenant <YOUR_TENNANT>`


## Running the Project
To run the project locally, follow these steps:
1. Clone the repository.
2. Don't forget `az login --tenant <YOUR_TENNANT>`
3. Setup local configuration according to `Required configuration for local development` section
2. Rebuild and run the project.

Pass following as command line arguments or Evironment variable
--MSMQ_CONNECTION_STRING your_connection_string 
--SERVICE_BUS_CONNECTION_STRING your_connection_string 
--SERVICE_BUS_QUEUENAME your_queue_name"

