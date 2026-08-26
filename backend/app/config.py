from functools import lru_cache

from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    model_config = SettingsConfigDict(env_file=".env", env_file_encoding="utf-8", extra="ignore")

    # Database
    database_url: str = "postgresql+asyncpg://postgres:postgres@db:5432/employee_agent"

    # JWT
    jwt_secret_key: str
    jwt_algorithm: str = "HS256"
    access_token_expire_minutes: int = 15
    refresh_token_expire_days: int = 7

    # Cookies
    cookie_secure: bool = True
    cookie_domain: str | None = None
    # "lax" works when the dashboard and API share an origin (local dev via
    # the Vite proxy, or same-domain production). A dashboard hosted on a
    # different origin than the API needs "none" instead - browsers refuse
    # to attach Lax cookies to cross-site fetch/XHR requests at all (only to
    # top-level navigations), which would silently break every authenticated
    # request. "none" requires cookie_secure=True (browsers reject
    # SameSite=None without Secure).
    cookie_samesite: str = "lax"

    # CORS
    cors_origins: list[str] = ["http://localhost:5173"]

    # SMTP (alert emails)
    smtp_host: str = "localhost"
    smtp_port: int = 587
    smtp_username: str = ""
    smtp_password: str = ""
    smtp_from_address: str = "employee-agent@example.com"
    smtp_use_tls: bool = True

    # Scheduler
    aggregation_interval_minutes: int = 15
    alert_evaluation_interval_minutes: int = 5

    # App
    environment: str = "development"


@lru_cache
def get_settings() -> Settings:
    return Settings()
