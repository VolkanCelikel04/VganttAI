import type { ReactNode } from "react";

type StatusMessageProps = {
  tone?: "success" | "error" | "info" | "warning";
  children?: ReactNode;
};

export function StatusMessage({ tone = "info", children }: StatusMessageProps) {
  if (!children) {
    return null;
  }

  return (
    <div className={`status-message status-message--${tone}`} role="status">
      {children}
    </div>
  );
}
