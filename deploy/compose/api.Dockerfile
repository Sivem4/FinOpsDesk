FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/backend/FinOpsDesk.SharedKernel/FinOpsDesk.SharedKernel.csproj src/backend/FinOpsDesk.SharedKernel/
COPY src/backend/Modules/FinOpsDesk.Modules.Access/FinOpsDesk.Modules.Access.csproj src/backend/Modules/FinOpsDesk.Modules.Access/
COPY src/backend/Modules/FinOpsDesk.Modules.Reconciliation/FinOpsDesk.Modules.Reconciliation.csproj src/backend/Modules/FinOpsDesk.Modules.Reconciliation/
COPY src/backend/FinOpsDesk.Api/FinOpsDesk.Api.csproj src/backend/FinOpsDesk.Api/
RUN dotnet restore src/backend/FinOpsDesk.Api/FinOpsDesk.Api.csproj
COPY src/backend/FinOpsDesk.SharedKernel/ src/backend/FinOpsDesk.SharedKernel/
COPY src/backend/Modules/FinOpsDesk.Modules.Access/ src/backend/Modules/FinOpsDesk.Modules.Access/
COPY src/backend/Modules/FinOpsDesk.Modules.Reconciliation/ src/backend/Modules/FinOpsDesk.Modules.Reconciliation/
COPY src/backend/FinOpsDesk.Api/ src/backend/FinOpsDesk.Api/
RUN dotnet publish src/backend/FinOpsDesk.Api/FinOpsDesk.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:1a48394b6f29d3a32c79d9edecd4abda0ecbe8776d567dbfc4590ed6979c740c
USER root
RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app ./
COPY deploy/compose/api-entrypoint.sh /opt/finopsdesk/api-entrypoint
RUN chmod 0755 /opt/finopsdesk/api-entrypoint
USER $APP_UID
EXPOSE 8443
ENTRYPOINT ["/opt/finopsdesk/api-entrypoint"]
