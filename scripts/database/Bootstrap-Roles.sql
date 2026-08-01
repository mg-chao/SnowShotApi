\set ON_ERROR_STOP on

SELECT 'CREATE ROLE snowshot_migrator LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS'
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'snowshot_migrator') \gexec
SELECT 'CREATE ROLE snowshot_api LOGIN NOINHERIT NOSUPERUSER NOCREATEDB NOCREATEROLE NOREPLICATION NOBYPASSRLS'
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'snowshot_api') \gexec

ALTER ROLE snowshot_migrator PASSWORD :'migrator_password';
ALTER ROLE snowshot_api PASSWORD :'api_password';
ALTER ROLE snowshot_migrator SET search_path = snowshot, pg_catalog;
ALTER ROLE snowshot_api SET search_path = snowshot, pg_catalog;

REVOKE CONNECT ON DATABASE :"database_name" FROM PUBLIC;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT CONNECT, CREATE ON DATABASE :"database_name" TO snowshot_migrator;
GRANT CONNECT ON DATABASE :"database_name" TO snowshot_api;
REVOKE CREATE, TEMPORARY ON DATABASE :"database_name" FROM snowshot_api;
