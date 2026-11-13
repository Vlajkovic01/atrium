import { useEffect, useId, useRef, useState } from "react";
import { useTranslation } from "react-i18next";
import { useLocation, useNavigate } from "react-router";

import { formatAgo } from "../i18n.ts";
import { notificationText, useNotificationActions, useNotifications } from "../notifications.ts";
import { Icon } from "./Icon.tsx";

/**
 * The bell beside the logo (D-045): how many notifications are unread,
 * and the list on a click. Opening one goes where it points and marks it
 * read; the list keeps what was read until the person removes it.
 */
export function NotificationBell() {
  const { t } = useTranslation();
  const navigate = useNavigate();
  const location = useLocation();
  const id = useId();
  const root = useRef<HTMLDivElement>(null);
  const [open, setOpen] = useState(false);
  const list = useNotifications();
  const actions = useNotificationActions();
  const items = list.data?.items ?? [];
  const unread = Number(list.data?.unread ?? 0);

  useEffect(() => {
    if (!open) {
      return;
    }

    const away = (event: MouseEvent): void => {
      if (!root.current?.contains(event.target as Node)) {
        setOpen(false);
      }
    };
    const escape = (event: KeyboardEvent): void => {
      if (event.key === "Escape") {
        setOpen(false);
      }
    };
    document.addEventListener("mousedown", away);
    document.addEventListener("keydown", escape);
    return () => {
      document.removeEventListener("mousedown", away);
      document.removeEventListener("keydown", escape);
    };
  }, [open]);
  useEffect(() => setOpen(false), [location.pathname]);

  return (
    <div className="bell" ref={root}>
      <button
        type="button"
        className="icon-button bell-button"
        aria-expanded={open}
        aria-controls={open ? `${id}-panel` : undefined}
        aria-label={unread > 0 ? t("notifications.openUnread", { count: unread }) : t("notifications.open")}
        onClick={() => setOpen(!open)}
      >
        <Icon name="bell" size={20} />
        {unread > 0 ? (
          <span className="count-badge" aria-hidden="true">
            {unread > 99 ? "99+" : unread}
          </span>
        ) : null}
      </button>
      {open ? (
        <section id={`${id}-panel`} className="bell-panel" aria-label={t("notifications.title")}>
          <header className="bell-head">
            <h2>{t("notifications.title")}</h2>
            {unread > 0 ? (
              <button type="button" className="link-button" onClick={() => void actions.markRead(null)}>
                {t("notifications.markAllRead")}
              </button>
            ) : null}
            {items.length > 0 ? (
              <button type="button" className="link-button" onClick={() => void actions.clear()}>
                {t("notifications.clear")}
              </button>
            ) : null}
          </header>
          {items.length === 0 ? <p className="muted bell-empty">{t("notifications.empty")}</p> : null}
          <ul className="bell-list">
            {items.map((item) => (
              <li key={item.id} className={item.readAt ? "bell-item" : "bell-item is-unread"}>
                <button
                  type="button"
                  className="bell-open"
                  onClick={() => {
                    if (!item.readAt) {
                      void actions.markRead([item.id]);
                    }

                    if (item.link) {
                      void navigate(item.link);
                    }
                  }}
                >
                  <span className="bell-text">{notificationText(t, item)}</span>
                  <span className="bell-time">{formatAgo(item.createdAt)}</span>
                </button>
                <button
                  type="button"
                  className="icon-button"
                  aria-label={t("notifications.remove")}
                  onClick={() => void actions.remove(item.id)}
                >
                  <Icon name="close" size={14} />
                </button>
              </li>
            ))}
          </ul>
        </section>
      ) : null}
    </div>
  );
}
