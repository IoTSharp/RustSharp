// frozen P1 fixture: mir-family
enum Reading {
    Empty,
    Sample(i32),
    Pair { left: i32, right: i32 },
}

fn score(value: Reading) -> i32 {
    match value {
        Reading::Empty => 0,
        Reading::Sample(number) => number,
        Reading::Pair { left, right } => left + right,
    }
}

fn main() {
    println!("{}", score(Reading::Pair { left: 3, right: 4 }));
}
