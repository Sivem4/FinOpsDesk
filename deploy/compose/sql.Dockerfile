FROM mcr.microsoft.com/mssql/server:2022-CU22-ubuntu-22.04@sha256:0074f43a75e11b5d3a27cd7f4325332bf8ae7861255b0b39e4b2d374fe5f449d

USER root
COPY deploy/compose/sql-entrypoint.sh /opt/finopsdesk/sql-entrypoint
COPY deploy/compose/sql-ready.sh /opt/finopsdesk/sql-ready
COPY deploy/compose/sql-histories.sh /opt/finopsdesk/sql-histories
RUN chmod 0755 /opt/finopsdesk/sql-entrypoint /opt/finopsdesk/sql-ready /opt/finopsdesk/sql-histories
USER mssql
ENTRYPOINT ["/opt/finopsdesk/sql-entrypoint"]
CMD ["/opt/mssql/bin/sqlservr"]
