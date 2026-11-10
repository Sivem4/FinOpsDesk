FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:212fe4cb74627d5adf9817a4088906ae3edff1bd853bb27565d4b46731efe61e AS build
WORKDIR /src
COPY Directory.Build.props ./
COPY src/backend/FinOpsDesk.SharedKernel/FinOpsDesk.SharedKernel.csproj src/backend/FinOpsDesk.SharedKernel/
COPY src/backend/Modules/FinOpsDesk.Modules.Access/FinOpsDesk.Modules.Access.csproj src/backend/Modules/FinOpsDesk.Modules.Access/
COPY src/backend/Modules/FinOpsDesk.Modules.Reconciliation/FinOpsDesk.Modules.Reconciliation.csproj src/backend/Modules/FinOpsDesk.Modules.Reconciliation/
COPY src/backend/FinOpsDesk.ComposeMigrate/FinOpsDesk.ComposeMigrate.csproj src/backend/FinOpsDesk.ComposeMigrate/
RUN dotnet restore src/backend/FinOpsDesk.ComposeMigrate/FinOpsDesk.ComposeMigrate.csproj
COPY src/backend/FinOpsDesk.SharedKernel/ src/backend/FinOpsDesk.SharedKernel/
COPY src/backend/Modules/FinOpsDesk.Modules.Access/ src/backend/Modules/FinOpsDesk.Modules.Access/
COPY src/backend/Modules/FinOpsDesk.Modules.Reconciliation/ src/backend/Modules/FinOpsDesk.Modules.Reconciliation/
COPY src/backend/FinOpsDesk.ComposeMigrate/ src/backend/FinOpsDesk.ComposeMigrate/
RUN dotnet publish src/backend/FinOpsDesk.ComposeMigrate/FinOpsDesk.ComposeMigrate.csproj -c Release -o /app --no-restore

# runtimeconfig.json requires Microsoft.NETCore.App 10.0.0 and Microsoft.AspNetCore.App 10.0.0.
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:1a48394b6f29d3a32c79d9edecd4abda0ecbe8776d567dbfc4590ed6979c740c
USER root
WORKDIR /app
COPY --from=build /app ./
COPY deploy/compose/migrate-entrypoint.sh /opt/finopsdesk/migrate-entrypoint
RUN chmod 0755 /opt/finopsdesk/migrate-entrypoint
USER $APP_UID
ENTRYPOINT ["/opt/finopsdesk/migrate-entrypoint"]
