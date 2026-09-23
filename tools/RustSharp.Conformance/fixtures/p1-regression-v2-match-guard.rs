fn choose(value: (bool, i32)) -> i32 {
    match value {
        (true, x) if x > 0 => x,
        (true, x) => x + 1,
        (false, x) => x - 1,
    }
}

fn main() {
    println!("{}", choose((true, 7)));
}
