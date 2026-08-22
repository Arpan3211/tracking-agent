import uuid

from fastapi import APIRouter, Depends, HTTPException, Query, status
from sqlalchemy import select
from sqlalchemy.ext.asyncio import AsyncSession

from app.api.deps import get_current_user, require_roles
from app.core.csrf import verify_csrf
from app.core.security import hash_password
from app.database import get_db
from app.models.audit_log import AuditLog
from app.models.policy import Policy
from app.models.user import User, UserRole
from app.schemas.audit_log import AuditLogOut
from app.schemas.policy import PolicyCreate, PolicyOut, PolicyUpdate
from app.schemas.user import UserCreate, UserOut, UserUpdate
from app.services.audit import record_audit

# Every route here is Admin-only, not HR - user role/supervisor assignment
# and policy thresholds are more sensitive than the read-only dashboard
# scoping HR also gets. Revisit if HR is meant to manage policies too.
router = APIRouter(prefix="/admin", tags=["admin"], dependencies=[Depends(require_roles(UserRole.admin))])


# ---------------------------------------------------------------- users ----


@router.get("/users", response_model=list[UserOut])
async def list_users(db: AsyncSession = Depends(get_db)) -> list[User]:
    result = await db.execute(select(User).order_by(User.email))
    return list(result.scalars().all())


@router.post(
    "/users", response_model=UserOut, status_code=status.HTTP_201_CREATED, dependencies=[Depends(verify_csrf)]
)
async def create_user(
    payload: UserCreate,
    current_user: User = Depends(get_current_user),
    db: AsyncSession = Depends(get_db),
) -> User:
    existing = (await db.execute(select(User).where(User.email == payload.email))).scalar_one_or_none()
    if existing is not None:
        raise HTTPException(status_code=status.HTTP_400_BAD_REQUEST, detail="Email already registered")

    user = User(
        email=payload.email,
        hashed_password=hash_password(payload.password),
        full_name=payload.full_name,
        role=payload.role,
        supervisor_id=payload.supervisor_id,
    )
    db.add(user)
    await record_audit(db, current_user.id, "create_user", target=payload.email)
    await db.commit()
    await db.refresh(user)
    return user


@router.patch("/users/{user_id}", response_model=UserOut, dependencies=[Depends(verify_csrf)])
async def update_user(
    user_id: uuid.UUID,
    payload: UserUpdate,
    current_user: User = Depends(get_current_user),
    db: AsyncSession = Depends(get_db),
) -> User:
    user = await db.get(User, user_id)
    if user is None:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="User not found")

    data = payload.model_dump(exclude_unset=True)
    password = data.pop("password", None)
    if password:
        user.hashed_password = hash_password(password)
    for field, value in data.items():
        setattr(user, field, value)

    await record_audit(db, current_user.id, "update_user", target=str(user_id))
    await db.commit()
    await db.refresh(user)
    return user


# -------------------------------------------------------------- policies ----


@router.get("/policies", response_model=list[PolicyOut])
async def list_policies(db: AsyncSession = Depends(get_db)) -> list[Policy]:
    result = await db.execute(select(Policy).order_by(Policy.created_at.desc()))
    return list(result.scalars().all())


@router.post(
    "/policies", response_model=PolicyOut, status_code=status.HTTP_201_CREATED, dependencies=[Depends(verify_csrf)]
)
async def create_policy(
    payload: PolicyCreate,
    current_user: User = Depends(get_current_user),
    db: AsyncSession = Depends(get_db),
) -> Policy:
    policy = Policy(
        name=payload.name,
        rule_type=payload.rule_type,
        threshold_value=payload.threshold_value,
        is_active=payload.is_active,
        created_by=current_user.id,
    )
    db.add(policy)
    await record_audit(db, current_user.id, "create_policy", target=payload.name)
    await db.commit()
    await db.refresh(policy)
    return policy


@router.patch("/policies/{policy_id}", response_model=PolicyOut, dependencies=[Depends(verify_csrf)])
async def update_policy(
    policy_id: uuid.UUID,
    payload: PolicyUpdate,
    current_user: User = Depends(get_current_user),
    db: AsyncSession = Depends(get_db),
) -> Policy:
    policy = await db.get(Policy, policy_id)
    if policy is None:
        raise HTTPException(status_code=status.HTTP_404_NOT_FOUND, detail="Policy not found")

    for field, value in payload.model_dump(exclude_unset=True).items():
        setattr(policy, field, value)

    await record_audit(db, current_user.id, "update_policy", target=str(policy_id))
    await db.commit()
    await db.refresh(policy)
    return policy


# ------------------------------------------------------------- audit log ----


@router.get("/audit-log", response_model=list[AuditLogOut])
async def get_audit_log(
    db: AsyncSession = Depends(get_db),
    limit: int = Query(default=200, le=1000),
) -> list[AuditLog]:
    result = await db.execute(select(AuditLog).order_by(AuditLog.timestamp.desc()).limit(limit))
    return list(result.scalars().all())
