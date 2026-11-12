import { useEffect, useId, useRef, useState, type KeyboardEvent } from "react";

import { Icon, type IconName } from "./Icon.tsx";

export interface SelectOption<T extends string> {
  readonly value: T;
  readonly label: string;
}

/**
 * A dropdown drawn by Atrium, not by the browser. A native select can be
 * styled while closed, but its open list is drawn by the operating system
 * and looks foreign. This one follows the standard listbox pattern, so it
 * works with the keyboard and with screen readers: arrows move, Enter or
 * Space chooses, Escape closes.
 */
export function Select<T extends string>(props: {
  readonly label: string;
  readonly hideLabel?: boolean;
  readonly value: T;
  readonly options: readonly SelectOption<T>[];
  readonly onChange: (value: T) => void;
  readonly disabled?: boolean;
  readonly icon?: IconName;
  readonly variant?: "field" | "pill" | "inline";
}) {
  const { options, value, variant = "field" } = props;
  const id = useId();
  const labelId = `${id}-label`;
  const buttonId = `${id}-button`;
  const root = useRef<HTMLDivElement>(null);
  const [open, setOpen] = useState(false);
  const selectedIndex = Math.max(
    0,
    options.findIndex((option) => option.value === value),
  );
  const [active, setActive] = useState(selectedIndex);

  // Close when the user clicks or moves focus anywhere else.
  useEffect(() => {
    if (!open) {
      return;
    }

    const onPointer = (event: MouseEvent): void => {
      if (!root.current?.contains(event.target as Node)) {
        setOpen(false);
      }
    };
    document.addEventListener("mousedown", onPointer);
    return () => document.removeEventListener("mousedown", onPointer);
  }, [open]);

  const show = (): void => {
    setActive(selectedIndex);
    setOpen(true);
  };

  const choose = (index: number): void => {
    const option = options[index];
    setOpen(false);
    if (option && option.value !== value) {
      props.onChange(option.value);
    }
  };

  const onKeyDown = (event: KeyboardEvent<HTMLButtonElement>): void => {
    const last = options.length - 1;
    switch (event.key) {
      case "ArrowDown":
      case "ArrowUp": {
        event.preventDefault();
        if (!open) {
          show();
        } else {
          const step = event.key === "ArrowDown" ? 1 : -1;
          setActive((current) => Math.min(last, Math.max(0, current + step)));
        }
        break;
      }
      case "Home":
      case "End":
        if (open) {
          event.preventDefault();
          setActive(event.key === "Home" ? 0 : last);
        }
        break;
      case "Enter":
      case " ":
        event.preventDefault();
        if (open) {
          choose(active);
        } else {
          show();
        }
        break;
      case "Escape":
        if (open) {
          event.preventDefault();
          setOpen(false);
        }
        break;
      case "Tab":
        setOpen(false);
        break;
    }
  };

  return (
    <div className={`select select-${variant}`} ref={root}>
      <span id={labelId} className={props.hideLabel ? "visually-hidden" : "select-label"}>
        {props.label}
      </span>
      <button
        type="button"
        id={buttonId}
        className="select-button"
        role="combobox"
        aria-haspopup="listbox"
        aria-expanded={open}
        aria-controls={`${id}-list`}
        aria-labelledby={`${labelId} ${buttonId}`}
        aria-activedescendant={open ? `${id}-option-${active}` : undefined}
        disabled={props.disabled}
        onClick={() => (open ? setOpen(false) : show())}
        onKeyDown={onKeyDown}
      >
        {props.icon ? <Icon name={props.icon} size={16} /> : null}
        <span className="select-value">{options[selectedIndex]?.label}</span>
        <span className="select-arrow">
          <Icon name="chevronDown" size={16} />
        </span>
      </button>
      {open ? (
        <ul className="select-list" id={`${id}-list`} role="listbox" aria-labelledby={labelId}>
          {options.map((option, index) => (
            <li
              key={option.value}
              id={`${id}-option-${index}`}
              role="option"
              aria-selected={option.value === value}
              className={index === active ? "select-option is-active" : "select-option"}
              onMouseEnter={() => setActive(index)}
              // Keep focus on the button, so the keyboard keeps working after a click.
              onMouseDown={(event) => event.preventDefault()}
              onClick={() => choose(index)}
            >
              <span>{option.label}</span>
              {option.value === value ? <Icon name="check" size={16} /> : null}
            </li>
          ))}
        </ul>
      ) : null}
    </div>
  );
}
