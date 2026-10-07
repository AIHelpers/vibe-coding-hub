---
name: machine-learning
description: Build, train and evaluate ML models reproducibly (data splits, baselines, metrics, experiment tracking)
tags: [ml, data]
version: 1
---
# Machine learning

1. Restate the task as an ML problem: target, input features, metric that matches the business goal, and a trivial baseline.
2. Data: check leakage (time, duplicates, target-derived features), split train/validation/test once with a fixed seed, and keep the test set untouched until the end.
3. Start with the simplest model that can work (linear/GBM) and the baseline; only then try heavier models.
4. Make runs reproducible: seeds, pinned dependencies, config files for hyperparameters, data versions recorded.
5. Report validation metrics with confidence intervals or several seeds, error analysis on the worst slices, and the final test-set number once.
6. Keep notebooks for exploration; move training/evaluation code into modules with tests for the data transforms.
