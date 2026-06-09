import argparse
import json
import logging
import random
import signal
import sys
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from dataclasses import dataclass, field
from typing import List, Optional

import requests

logging.basicConfig(
    level=logging.INFO,
    format="%(asctime)s [%(levelname)s] %(message)s",
    datefmt="%Y-%m-%d %H:%M:%S",
)
logger = logging.getLogger("zigbee_simulator")


@dataclass
class AnodeEffectPrecursor:
    active: bool = False
    phase: int = 0
    remaining_ticks: int = 0
    voltage_rise_rate: float = 0.0
    noise_amplifier: float = 1.0
    frequency_shift: float = 0.0


@dataclass
class ConcentrationDropEvent:
    active: bool = False
    remaining_ticks: int = 0
    drop_rate: float = 0.0
    suppress_feeding: bool = False


@dataclass
class PotState:
    pot_id: str
    pot_number: int
    base_voltage: float
    alumina_concentration: float
    temperature: float
    bath_temperature: float
    aluminum_level: float
    bath_level: float
    anode_currents: List[float] = field(default_factory=list)
    voltage_drift: float = 0.0
    temp_drift: float = 0.0
    bath_temp_drift: float = 0.0
    al_level_drift: float = 0.0
    bath_level_drift: float = 0.0
    concentration_trend: float = 0.0
    is_low_concentration: bool = False
    is_rapid_drop: bool = False
    feeding_cooldown: int = 0
    voltage_spike_remaining: int = 0
    effect_precursor: AnodeEffectPrecursor = field(default_factory=AnodeEffectPrecursor)
    conc_drop_event: ConcentrationDropEvent = field(default_factory=ConcentrationDropEvent)

    def __post_init__(self):
        if not self.anode_currents:
            self.anode_currents = self._init_anode_currents()

    def _init_anode_currents(self) -> List[float]:
        base = random.uniform(30.0, 35.0)
        imbalance_center = random.randint(0, 23)
        currents = []
        for i in range(24):
            dist = min(abs(i - imbalance_center), 24 - abs(i - imbalance_center))
            offset = dist * random.uniform(0.05, 0.15) * random.choice([-1, 1])
            currents.append(round(base + offset + random.uniform(-0.5, 0.5), 2))
        return currents


class InjectionScheduler:
    PHASE_DESCRIPTIONS = {
        1: "阳极效应前兆-电压缓升期",
        2: "阳极效应前兆-噪声增大期",
        3: "阳极效应前兆-电压跳变期",
        4: "阳极效应前兆-临界期",
    }

    def __init__(self, num_pots: int, scenario: str):
        self.num_pots = num_pots
        self.scenario = scenario
        self.tick = 0
        self.next_injection_tick = 0
        self._schedule_next_injection()

    def _schedule_next_injection(self):
        if self.scenario == "normal":
            return
        self.next_injection_tick = self.tick + random.randint(20, 80)

    def should_inject(self) -> bool:
        if self.scenario == "normal":
            return False
        if self.tick >= self.next_injection_tick:
            self._schedule_next_injection()
            return True
        return False

    def get_injection_targets(self, pots: List[PotState], event_type: str) -> List[int]:
        available = [i for i, p in enumerate(pots)
                     if not p.effect_precursor.active and not p.conc_drop_event.active]
        if not available:
            return []

        if event_type == "anode_effect_precursor":
            count = random.randint(1, max(1, len(available) // 50))
            return random.sample(available, min(count, len(available)))
        elif event_type == "concentration_drop":
            count = random.randint(2, max(2, len(available) // 20))
            return random.sample(available, min(count, len(available)))
        return []

    def choose_event_type(self) -> str:
        if self.scenario == "anode_effect":
            return "anode_effect_precursor"
        elif self.scenario == "low_concentration":
            return "concentration_drop"
        elif self.scenario == "mixed":
            return random.choice(["anode_effect_precursor", "concentration_drop",
                                  "concentration_drop", "concentration_drop"])
        return "concentration_drop"

    def advance(self):
        self.tick += 1


class PotSimulator:
    def __init__(self, num_pots: int, scenario: str):
        self.num_pots = num_pots
        self.scenario = scenario
        self.pots: List[PotState] = []
        self.tick = 0
        self.injector = InjectionScheduler(num_pots, scenario)
        self._init_pots()

    def _init_pots(self):
        low_conc_count = 0
        rapid_drop_count = 0

        if self.scenario in ("low_concentration", "mixed"):
            low_conc_count = random.randint(
                int(self.num_pots * 0.05), int(self.num_pots * 0.10)
            )
        if self.scenario in ("anode_effect", "mixed"):
            rapid_drop_count = random.randint(1, 3) if self.num_pots >= 10 else 1

        low_conc_indices = set(random.sample(range(self.num_pots), low_conc_count)) if low_conc_count else set()
        remaining = set(range(self.num_pots)) - low_conc_indices
        rapid_drop_indices = set(random.sample(list(remaining), rapid_drop_count)) if rapid_drop_count and remaining else set()

        for i in range(self.num_pots):
            pot_id = f"P-{i + 1:03d}"

            if i in low_conc_indices:
                concentration = random.uniform(1.0, 1.8)
            elif i in rapid_drop_indices:
                concentration = random.uniform(2.0, 2.8)
            else:
                concentration = random.uniform(2.0, 3.5)

            base_voltage = random.uniform(4.0, 4.5)
            temperature = random.uniform(950.0, 970.0)
            bath_temperature = temperature + random.uniform(2.0, 8.0)
            aluminum_level = random.uniform(20.0, 30.0)
            bath_level = random.uniform(18.0, 25.0)

            pot = PotState(
                pot_id=pot_id,
                pot_number=i + 1,
                base_voltage=base_voltage,
                alumina_concentration=concentration,
                temperature=temperature,
                bath_temperature=bath_temperature,
                aluminum_level=aluminum_level,
                bath_level=bath_level,
                is_low_concentration=i in low_conc_indices,
                is_rapid_drop=i in rapid_drop_indices,
            )

            if pot.is_rapid_drop:
                pot.concentration_trend = -random.uniform(0.3, 0.6)
                pot.effect_precursor = AnodeEffectPrecursor(
                    active=True, phase=1,
                    remaining_ticks=random.randint(30, 80),
                    voltage_rise_rate=random.uniform(0.005, 0.015),
                    noise_amplifier=random.uniform(1.5, 2.5),
                )

            if i in low_conc_indices:
                pot.conc_drop_event = ConcentrationDropEvent(
                    active=True,
                    remaining_ticks=random.randint(20, 60),
                    drop_rate=random.uniform(0.15, 0.35),
                    suppress_feeding=random.random() < 0.3,
                )

            self.pots.append(pot)

    def advance(self):
        self.tick += 1
        self.injector.advance()
        hours_per_tick = 15.0 / 3600.0

        if self.injector.should_inject():
            event_type = self.injector.choose_event_type()
            targets = self.injector.get_injection_targets(self.pots, event_type)
            for idx in targets:
                self._inject_event(self.pots[idx], event_type)

        for pot in self.pots:
            pot.voltage_drift += random.uniform(-0.002, 0.002)
            pot.voltage_drift = max(-0.1, min(0.1, pot.voltage_drift))

            pot.temp_drift += random.uniform(-0.05, 0.05)
            pot.temp_drift = max(-5.0, min(5.0, pot.temp_drift))

            pot.bath_temp_drift += random.uniform(-0.05, 0.05)
            pot.bath_temp_drift = max(-5.0, min(5.0, pot.bath_temp_drift))

            pot.al_level_drift += random.uniform(-0.01, 0.01)
            pot.al_level_drift = max(-2.0, min(2.0, pot.al_level_drift))

            pot.bath_level_drift += random.uniform(-0.01, 0.01)
            pot.bath_level_drift = max(-2.0, min(2.0, pot.bath_level_drift))

            consumption_rate = 0.1 * hours_per_tick
            if pot.is_rapid_drop:
                consumption_rate *= random.uniform(3.0, 6.0)

            if pot.conc_drop_event.active:
                consumption_rate += pot.conc_drop_event.drop_rate * hours_per_tick
                pot.conc_drop_event.remaining_ticks -= 1
                if pot.conc_drop_event.remaining_ticks <= 0:
                    pot.conc_drop_event.active = False
                    logger.info("浓度下降事件结束: %s", pot.pot_id)

            pot.alumina_concentration -= consumption_rate

            if pot.alumina_concentration < 1.8 and pot.feeding_cooldown <= 0:
                if not pot.conc_drop_event.suppress_feeding:
                    feed_amount = random.uniform(0.8, 1.5)
                    pot.alumina_concentration += feed_amount
                    pot.feeding_cooldown = random.randint(8, 20)
                    logger.info(
                        "FEEDING: %s concentration=%.2f%% -> %.2f%% (feed +%.2f%%)",
                        pot.pot_id,
                        pot.alumina_concentration - feed_amount,
                        pot.alumina_concentration,
                        feed_amount,
                    )

            if pot.feeding_cooldown > 0:
                pot.feeding_cooldown -= 1

            if pot.alumina_concentration < 1.8:
                if not pot.is_low_concentration:
                    logger.warning(
                        "LOW CONCENTRATION: %s at %.2f%%", pot.pot_id, pot.alumina_concentration
                    )
                    pot.is_low_concentration = True

            if pot.alumina_concentration < 1.0:
                logger.warning(
                    "CRITICAL: %s concentration %.2f%% - approaching anode effect!",
                    pot.pot_id,
                    pot.alumina_concentration,
                )

            pot.alumina_concentration = max(0.3, pot.alumina_concentration)

            if pot.effect_precursor.active:
                self._advance_precursor(pot)
            else:
                if pot.voltage_spike_remaining > 0:
                    pot.voltage_spike_remaining -= 1
                elif self.scenario in ("anode_effect", "mixed"):
                    if random.random() < 0.002:
                        pot.voltage_spike_remaining = random.randint(1, 4)
                        logger.warning(
                            "VOLTAGE SPIKE: %s - approaching anode effect!", pot.pot_id
                        )

            for i in range(24):
                noise_scale = pot.effect_precursor.noise_amplifier if pot.effect_precursor.active else 1.0
                pot.anode_currents[i] += random.uniform(-0.1, 0.1) * noise_scale
                pot.anode_currents[i] = max(26.0, min(40.0, pot.anode_currents[i]))

    def _inject_event(self, pot: PotState, event_type: str):
        if event_type == "anode_effect_precursor":
            pot.effect_precursor = AnodeEffectPrecursor(
                active=True,
                phase=1,
                remaining_ticks=random.randint(40, 120),
                voltage_rise_rate=random.uniform(0.005, 0.02),
                noise_amplifier=random.uniform(1.5, 3.0),
                frequency_shift=random.uniform(0.1, 0.3),
            )
            pot.is_rapid_drop = True
            pot.concentration_trend = -random.uniform(0.3, 0.6)
            logger.info(
                "注入阳极效应前兆: %s (持续%d ticks, 电压上升率=%.4f/tick)",
                pot.pot_id, pot.effect_precursor.remaining_ticks,
                pot.effect_precursor.voltage_rise_rate,
            )
        elif event_type == "concentration_drop":
            pot.conc_drop_event = ConcentrationDropEvent(
                active=True,
                remaining_ticks=random.randint(20, 80),
                drop_rate=random.uniform(0.2, 0.5),
                suppress_feeding=random.random() < 0.4,
            )
            logger.info(
                "注入浓度下降事件: %s (持续%d ticks, 下降率=%.3f/tick, 抑制补料=%s)",
                pot.pot_id, pot.conc_drop_event.remaining_ticks,
                pot.conc_drop_event.drop_rate,
                pot.conc_drop_event.suppress_feeding,
            )

    def _advance_precursor(self, pot: PotState):
        precursor = pot.effect_precursor
        precursor.remaining_ticks -= 1

        if precursor.remaining_ticks <= 0:
            precursor.active = False
            pot.is_rapid_drop = False
            pot.concentration_trend = 0.0
            logger.info("阳极效应前兆消退: %s", pot.pot_id)
            return

        total_ticks = precursor.remaining_ticks + 1
        if total_ticks > 80:
            precursor.phase = 1
        elif total_ticks > 40:
            precursor.phase = 2
            precursor.noise_amplifier = min(precursor.noise_amplifier * 1.01, 5.0)
        elif total_ticks > 15:
            precursor.phase = 3
            precursor.voltage_rise_rate *= 1.02
            precursor.noise_amplifier = min(precursor.noise_amplifier * 1.02, 8.0)
        else:
            precursor.phase = 4
            precursor.voltage_rise_rate *= 1.05
            precursor.noise_amplifier = min(precursor.noise_amplifier * 1.05, 15.0)
            pot.voltage_spike_remaining = max(pot.voltage_spike_remaining, 1)

        pot.base_voltage += precursor.voltage_rise_rate

        if precursor.phase >= 3 and random.random() < 0.1:
            logger.warning(
                "%s: %s (phase=%d, voltage_rise=%.4f, noise=%.1fx)",
                InjectionScheduler.PHASE_DESCRIPTIONS.get(precursor.phase, ""),
                pot.pot_id, precursor.phase,
                precursor.voltage_rise_rate, precursor.noise_amplifier,
            )

    def get_pot_data(self, pot: PotState) -> dict:
        voltage = pot.base_voltage + pot.voltage_drift + random.uniform(-0.05, 0.05)

        if pot.alumina_concentration < 1.5:
            voltage += (1.5 - pot.alumina_concentration) * 0.5

        if pot.effect_precursor.active:
            noise_amp = pot.effect_precursor.noise_amplifier
            voltage += random.uniform(-0.05, 0.05) * noise_amp

            if pot.effect_precursor.phase >= 2:
                voltage += random.uniform(0.0, 0.1) * (pot.effect_precursor.phase - 1)

        if pot.voltage_spike_remaining > 0:
            voltage += random.uniform(0.5, 2.0)
            if voltage > 8.0:
                logger.warning(
                    "HIGH VOLTAGE: %s at %.2fV", pot.pot_id, voltage
                )

        temperature = pot.temperature + pot.temp_drift + random.uniform(-0.3, 0.3)
        bath_temperature = pot.bath_temperature + pot.bath_temp_drift + random.uniform(-0.3, 0.3)
        aluminum_level = pot.aluminum_level + pot.al_level_drift + random.uniform(-0.1, 0.1)
        bath_level = pot.bath_level + pot.bath_level_drift + random.uniform(-0.1, 0.1)

        noise_scale = pot.effect_precursor.noise_amplifier if pot.effect_precursor.active else 1.0
        noisy_currents = [
            round(c + random.uniform(-0.2, 0.2) * noise_scale, 2) for c in pot.anode_currents
        ]

        return {
            "potId": pot.pot_number,
            "voltage": round(voltage, 3),
            "anodeCurrentDistribution": json.dumps(noisy_currents),
            "potTemperature": round(temperature, 1),
            "bathTemperature": round(bath_temperature, 1),
            "aluminumLevel": round(aluminum_level, 1),
            "bathLevel": round(bath_level, 1),
        }


def send_pot_data(url: str, data: dict) -> tuple:
    try:
        resp = requests.post(url, json=data, timeout=10)
        return data["potId"], resp.status_code
    except requests.RequestException as e:
        return data["potId"], str(e)


def main():
    parser = argparse.ArgumentParser(description="ZigBee Pot Data Simulator")
    parser.add_argument(
        "--url",
        default="http://localhost:5000",
        help="Backend base URL (default: http://localhost:5000)",
    )
    parser.add_argument(
        "--interval",
        type=int,
        default=15,
        help="Send interval in seconds (default: 15)",
    )
    parser.add_argument(
        "--pots",
        type=int,
        default=200,
        help="Number of pots to simulate (default: 200)",
    )
    parser.add_argument(
        "--scenario",
        choices=["normal", "low_concentration", "anode_effect", "mixed"],
        default="mixed",
        help="Simulation scenario (default: mixed)",
    )
    parser.add_argument(
        "--workers",
        type=int,
        default=20,
        help="Thread pool workers (default: 20)",
    )
    parser.add_argument(
        "--inject-interval",
        type=int,
        default=0,
        help="Override injection interval in ticks (0=auto, default: 0)",
    )
    args = parser.parse_args()

    endpoint = f"{args.url.rstrip('/')}/api/potdata/zigbee"
    logger.info(
        "Starting ZigBee simulator: %d pots, scenario=%s, interval=%ds, endpoint=%s",
        args.pots,
        args.scenario,
        args.interval,
        endpoint,
    )
    logger.info("注入场景说明:")
    logger.info("  normal          - 无注入,正常消耗")
    logger.info("  low_concentration - 定期注入浓度下降事件(加速消耗+可能抑制补料)")
    logger.info("  anode_effect    - 定期注入阳极效应前兆(4阶段:缓升->噪声->跳变->临界)")
    logger.info("  mixed           - 混合注入浓度下降和阳极效应前兆")

    sim = PotSimulator(args.pots, args.scenario)

    if args.inject_interval > 0:
        sim.injector.next_injection_tick = args.inject_interval

    running = True

    def handle_signal(signum, frame):
        nonlocal running
        logger.info("收到终止信号，正在停止...")
        running = False

    signal.signal(signal.SIGINT, handle_signal)
    signal.signal(signal.SIGTERM, handle_signal)

    with ThreadPoolExecutor(max_workers=args.workers) as executor:
        while running:
            sim.advance()
            payloads = [sim.get_pot_data(pot) for pot in sim.pots]

            futures = {
                executor.submit(send_pot_data, endpoint, payload): payload["potId"]
                for payload in payloads
            }

            success = 0
            errors = 0
            error_details: List[str] = []

            for future in as_completed(futures):
                pot_id, result = future.result()
                if result == 200 or result == 201:
                    success += 1
                else:
                    errors += 1
                    if len(error_details) < 5:
                        error_details.append(f"{pot_id}: {result}")

            active_precursors = sum(1 for p in sim.pots if p.effect_precursor.active)
            active_drops = sum(1 for p in sim.pots if p.conc_drop_event.active)

            logger.info(
                "Batch sent: tick=%d, pots=%d, success=%d, errors=%d%s | 前兆=%d 浓降=%d",
                sim.tick,
                args.pots,
                success,
                errors,
                f" | Sample errors: {error_details}" if error_details else "",
                active_precursors,
                active_drops,
            )

            for _ in range(args.interval):
                if not running:
                    break
                time.sleep(1)

    logger.info("模拟器已停止")


if __name__ == "__main__":
    main()
