import argparse
import json
import logging
import random
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
class PotState:
    pot_id: str
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


class PotSimulator:
    def __init__(self, num_pots: int, scenario: str):
        self.num_pots = num_pots
        self.scenario = scenario
        self.pots: List[PotState] = []
        self.tick = 0
        self._init_pots()

    def _init_pots(self):
        low_conc_count = 0
        rapid_drop_count = 0

        if self.scenario in ("low_concentration", "mixed"):
            low_conc_count = random.randint(
                int(self.num_pots * 0.05), int(self.num_pots * 0.10)
            )
        if self.scenario in ("anode_effect", "mixed"):
            rapid_drop_count = random.randint(1, 2) if self.num_pots >= 10 else 1

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

            self.pots.append(pot)

    def advance(self):
        self.tick += 1
        hours_per_tick = 15.0 / 3600.0

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

            pot.alumina_concentration -= consumption_rate

            if pot.alumina_concentration < 1.8 and pot.feeding_cooldown <= 0:
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

            if pot.voltage_spike_remaining > 0:
                pot.voltage_spike_remaining -= 1
            elif self.scenario in ("anode_effect", "mixed"):
                if random.random() < 0.002:
                    pot.voltage_spike_remaining = random.randint(1, 4)
                    logger.warning(
                        "VOLTAGE SPIKE: %s - approaching anode effect!", pot.pot_id
                    )

            for i in range(24):
                pot.anode_currents[i] += random.uniform(-0.1, 0.1)
                pot.anode_currents[i] = max(26.0, min(40.0, pot.anode_currents[i]))

    def get_pot_data(self, pot: PotState) -> dict:
        voltage = pot.base_voltage + pot.voltage_drift + random.uniform(-0.05, 0.05)

        if pot.alumina_concentration < 1.5:
            voltage += (1.5 - pot.alumina_concentration) * 0.5

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

        noisy_currents = [
            round(c + random.uniform(-0.2, 0.2), 2) for c in pot.anode_currents
        ]

        return {
            "potId": int(pot.pot_id.split("-")[1]),
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
    args = parser.parse_args()

    endpoint = f"{args.url.rstrip('/')}/api/potdata/zigbee"
    logger.info(
        "Starting ZigBee simulator: %d pots, scenario=%s, interval=%ds, endpoint=%s",
        args.pots,
        args.scenario,
        args.interval,
        endpoint,
    )

    sim = PotSimulator(args.pots, args.scenario)

    with ThreadPoolExecutor(max_workers=20) as executor:
        while True:
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

            logger.info(
                "Batch sent: tick=%d, pots=%d, success=%d, errors=%d%s",
                sim.tick,
                args.pots,
                success,
                errors,
                f" | Sample errors: {error_details}" if error_details else "",
            )

            time.sleep(args.interval)


if __name__ == "__main__":
    main()
