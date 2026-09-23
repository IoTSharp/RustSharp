fn choose(value: bool) -> i32 {
    match value {
        true | false => 1,
    }
}

fn main() {
    println!("{}", choose(false));
}
