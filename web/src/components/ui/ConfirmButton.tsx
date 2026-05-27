import type { ReactNode } from "react";

type ConfirmButtonProps = {
  children: ReactNode;
  className?: string;
  confirmText: string;
  disabled?: boolean;
  onConfirm: () => void;
};

export function ConfirmButton({
  children,
  className,
  confirmText,
  disabled,
  onConfirm
}: ConfirmButtonProps) {
  return (
    <button
      className={className}
      disabled={disabled}
      type="button"
      onClick={() => {
        if (window.confirm(confirmText)) {
          onConfirm();
        }
      }}
    >
      {children}
    </button>
  );
}
