-- Создаёт read-only роль для Web API Analytics-модуля (ADR-011 §«MVP trade-off»).
-- Работает в init-фазе Postgres-контейнера (ДО первого старта PG, пока миграции не применились).
-- GRANT-ы на конкретные таблицы выдаёт сама EF-миграция (Initial.cs) после CREATE TABLE.
-- Здесь только базовые права: подключение к БД и USAGE на схему.
--
-- Пароль на dev-стенде "захардкожен" — это локалка/закрытая VPS-сеть; на VPS сменить
-- через `docker compose exec processing-db psql -c "ALTER USER analytics_reader WITH PASSWORD '<new>';"`
-- или через ENV-substitution в этом скрипте.

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'analytics_reader') THEN
        CREATE ROLE analytics_reader LOGIN PASSWORD 'analytics_reader_pwd';
    END IF;
END $$;

GRANT CONNECT ON DATABASE processing TO analytics_reader;
GRANT USAGE ON SCHEMA public TO analytics_reader;
